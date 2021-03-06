﻿//-----------------------------------------------------------------------
// <copyright company="CoApp Project">
//     Copyright (c) 2010-2012 Garrett Serack and CoApp Contributors. 
//     Contributors can be discovered using the 'git log' command.
//     All rights reserved.
// </copyright>
// <license>
//     The software is licensed under the Apache 2.0 License (the "License")
//     You may not use the software except in compliance with the License. 
// </license>
//-----------------------------------------------------------------------

namespace CoApp.Packaging.Client {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Dynamic;
    using System.IO.Pipes;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Security.Principal;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Toolkit.Collections;
    using Toolkit.Exceptions;
    using Toolkit.Extensions;
    using Toolkit.ImpromptuInterface;
    using Toolkit.Logging;
    using Toolkit.Pipes;
    using Toolkit.Tasks;
    using Toolkit.Win32;

    public class Session : OutgoingCallDispatcher {
        internal class ManualEventQueue : Queue<UrlEncodedMessage>, IDisposable {
            internal static readonly IDictionary<int, ManualEventQueue> EventQueues = new XDictionary<int, ManualEventQueue>();
            internal readonly ManualResetEvent ResetEvent = new ManualResetEvent(true);
            internal bool StillWorking;
            
            public ManualEventQueue() {
                var tid = Task.CurrentId.GetValueOrDefault();
                if (tid == 0) {
                    throw new CoAppException("Cannot create a ManualEventQueue outside of a task.");
                }
                lock (EventQueues) {
                    EventQueues.Add(tid, this);
                }
            }

            public new void Enqueue(UrlEncodedMessage message) {
                base.Enqueue(message);
                ResetEvent.Set();
            }

            public void Dispose() {
                lock (EventQueues) {
                    EventQueues.Remove(Task.CurrentId.GetValueOrDefault());
                }
            }

            public static ManualEventQueue GetQueue(int taskId) {
                lock (EventQueues) {
                    return EventQueues.ContainsKey(taskId) ? EventQueues[taskId] : null;
                }
            }

            internal static void ResetAllQueues() {
                if (EventQueues.Any()) {
                    Logger.Warning("Forcing clearing out event queues in client library");
                    var oldQueues = EventQueues.Values.ToArray();
                    //EventQueues.Clear();
                    foreach (var q in oldQueues) {
                        q.StillWorking = false;
                        q.ResetEvent.Set();
                    }
                }
            }
        }
       
        internal const int BufferSize = 1024 * 1024 * 2;
        private bool? _isElevated;
        private bool IsElevated {
            get {
                if (!_isElevated.HasValue) {
                    _isElevated = false;

                    try {
                        var ntAuth = new SidIdentifierAuthority();
                        ntAuth.Value = new byte[] {0, 0, 0, 0, 0, 5};

                        var psid = IntPtr.Zero;
                        bool isAdmin;
                        if (Advapi32.AllocateAndInitializeSid(ref ntAuth, 2, 0x00000020, 0x00000220, 0, 0, 0, 0, 0, 0, out psid) && Advapi32.CheckTokenMembership(IntPtr.Zero, psid, out isAdmin) && isAdmin) {
                            _isElevated = true;
                        }
                    } catch {
                    }
                }

                return _isElevated.Value;
            }
        }
        private IPackageManager _remoteService;
        private static Session _instance = new Session();
        private static readonly ManualResetEvent _isBufferReady = new ManualResetEvent(false);
        private static readonly ManualResetEvent _isProcessingMessages = new ManualResetEvent(false);
        private Task _connectingTask;
        private int _autoConnectionCount;
        private string PipeName = "CoAppInstaller";
        private NamedPipeClientStream _pipe;

        static Session() {
            UrlEncodedMessage.AddTypeSubstitution<IPackage>((message, objectName, expectedType) => Package.GetPackage(message[objectName + ".CanonicalName"]));
        }

        internal static int ActiveCalls {
            get {
                return ManualEventQueue.EventQueues.Keys.Count;
            }
        }

        public static IPackageManager RemoteService { get {
            return _instance._remoteService;
        } }

        public static bool IsServiceAvailable {
            get {
                return EngineServiceManager.Available;
            }
        }

        public static bool IsConnected {
            get {
                return IsPipeConnected && _isProcessingMessages.WaitOne(0);
            }
        }

        public static bool IsPipeConnected {
            get {
                return IsServiceAvailable && _instance._pipe != null && _instance._pipe.IsConnected;
            }
        }

        private Session()
            : base(typeof(IPackageManager), WriteAsync) {
            _remoteService = this.ActLike();
        }

        /// <summary>
        ///   This dispatcher wraps the dispatch of the remote call in a Task (by continuing on the Connect()) which allows the client to continue working asynchronously while the service is doing it's thing.
        /// </summary>
        /// <param name="binder"> </param>
        /// <param name="args"> </param>
        /// <param name="result"> </param>
        /// <returns> </returns>
        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result) {
            if( PackageManagerResponseImpl.EngineRestarting ) {
                // don't send more calls until it's back.
                EngineServiceManager.WaitForStableMoment();
            }

            result = Connect().Continue(() => PerformCall(binder, args));
            return true;
        }

        private PackageManagerResponseImpl PerformCall( InvokeMemberBinder binder, object[] args ) {
            using (var eventQueue = new ManualEventQueue()) {
                // create return message handler
                var responseHandler = new PackageManagerResponseImpl();
                CurrentTask.Events += new GetCurrentRequestId(() => "" + Task.CurrentId);
                // unhook the old one if it's there.
                responseHandler.Clear();

                // send OG message here!
                object callResult;
                base.TryInvokeMember(binder, args, out callResult);

                // will return when the final message comes thru.
                eventQueue.StillWorking = true;

                while (eventQueue.StillWorking && eventQueue.ResetEvent.WaitOne()) {
                    eventQueue.ResetEvent.Reset();
                    while (eventQueue.Count > 0) {
                        if (!Event<GetResponseDispatcher>.RaiseFirst().DispatchSynchronous(eventQueue.Dequeue())) {
                            eventQueue.StillWorking = false;
                        }
                    }
                }
                
                if (PackageManagerResponseImpl.EngineRestarting) {
                    Logger.Message("Going to try and re issue the call.");
                    // Disconnect();
                    // the service is going to restart, let's call TryInvokeMember again.
                    EngineServiceManager.WaitForStableMoment();
                    Connect().Wait();
                    return PerformCall(binder, args);
                }
                
                // this returns the final response back via the Task<*> 
                return responseHandler;
            }
        }

        private static ManualResetEvent _okToConnect = new ManualResetEvent(true);

        internal static Task Elevate() {
            Logger.Message("Asking for Elevation");
            lock (_instance) {
                _okToConnect.WaitOne();
            
                if (_instance.IsElevated) {
                    Logger.Message("Currently Elevated, DONE.");
                    return "Elevated".AsResultTask();
                }
                _okToConnect.Reset();

                Logger.Message("Not Currently Elevated, Proceeding");

                var svcTask = Task.Factory.StartNew(EngineServiceManager.EnsureServiceIsResponding);

                var pipeName = "CoAppInstaller" + Process.GetCurrentProcess().Id.ToString().MD5Hash();

                return svcTask.Continue(() => {
                        // start elevation proxy
                        var proc = Process.Start(new ProcessStartInfo {
                            FileName = "CoApp.ElevationProxy.Exe",
                            Arguments = pipeName,
                            UseShellExecute = false, 
                        });

                        if (proc == null || proc.HasExited) {
                            _instance._isElevated = false;
                            _okToConnect.Set();
                            throw new CoAppException("Failed to elevate for service communication");
                        }

                        // Disconnect from old pipe asap.
                        Logger.Message("In Elevate--Disconnecting old connection");
                        _instance.Disconnect();

                        // change pipe name 
                        _instance.PipeName = pipeName;

                        _instance._isElevated = true;
                        _okToConnect.Set();
                }).ContinueWith(a2 => _instance.Connect());
            }
        }

        internal bool ExpectingRestart = false;

        private Task Connect(string clientName = null, string sessionId = null) {
            lock (this) {
                _okToConnect.WaitOne();

                if (IsConnected) {
                    return "Completed".AsResultTask();
                }

                clientName = clientName ?? Process.GetCurrentProcess().Id.ToString();

                if (_connectingTask == null) {
                    
                    _isBufferReady.Reset();

                    PackageManagerResponseImpl.EngineRestarting = false;
                    _connectingTask = Task.Factory.StartNew(() => {
                        EngineServiceManager.EnsureServiceIsResponding();
                        
                        sessionId = sessionId ?? Process.GetCurrentProcess().Id.ToString() + "/" + _autoConnectionCount++;

                        for (int count = 0; count < 25; count++) {
                            Logger.Message("Connecting...{0}",DateTime.Now.Ticks );
                            _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                            try {
                                _pipe.Connect(400);
                                _pipe.ReadMode = PipeTransmissionMode.Message;
                                break;
                            } catch {
                                // it's not connecting.
                                _pipe = null;
                            }
                        }

                        if (_pipe == null) {
                            throw new CoAppException("Unable to connect to CoApp Service");
                        }

                        StartSession(clientName, sessionId);
                        Task.Factory.StartNew(ProcessMessages, TaskCreationOptions.None).AutoManage();
                    }, TaskCreationOptions.AttachedToParent);
                }
            }
            return _connectingTask;
        }

        private void ProcessMessages() {
            var incomingMessage = new byte[BufferSize];
            _isBufferReady.Set();

            try {
                // tell others that we are indeed processing messages.
                _isProcessingMessages.Set();

                do {
                    // we need to wait for the buffer to become available.
                    _isBufferReady.WaitOne();
                    

                    // now we claim the buffer 
                    _isBufferReady.Reset();
                    Task<int> readTask;
                    readTask = _pipe.ReadAsync(incomingMessage, 0, BufferSize);
                    
                    readTask.ContinueWith(
                        antecedent => {
                            if (antecedent.IsCanceled || antecedent.IsFaulted || !IsConnected) {
                                if (antecedent.IsCanceled) {
                                    Logger.Message("Client/Session ReadTask is Cancelled");
                                } if (antecedent.IsFaulted) {
                                    Logger.Message("Client/Session ReadTask is Faulted : {0}", antecedent.Exception.GetType());
                                }
                                Disconnect();
                                return;
                            }
                            if (antecedent.Result > 0) {
                                var rawMessage = Encoding.UTF8.GetString(incomingMessage, 0, antecedent.Result);
                                var responseMessage = new UrlEncodedMessage(rawMessage);
                                var rqid = responseMessage["rqid"].ToInt32();

                                // lazy log the response (since we're at the end of this task)
                                Logger.Message("Response:[{0}]{1}".format(rqid, responseMessage.ToSmallerString()));

                                try {
                                    var queue = ManualEventQueue.GetQueue(rqid);
                                    if (queue != null) {
                                        queue.Enqueue(responseMessage);
                                    }
                                    //else {
                                    // GS01 : Need to put in protocol version detection.  
                                    //}
                                } catch {
                                }
                            }
                            // it's ok to let the next readTask use the buffer, we've got the data out & queued.
                            _isBufferReady.Set();
                        }).AutoManage();

                    // this wait just makes sure that we're only asking for one message at a time
                    // but does not throttle the messages themselves.
                    // readTask.Wait();
                } while (IsConnected);
            } catch (Exception e) {
                Logger.Message("Connection Terminating with Exception {0}/{1}", e.GetType(), e.Message);
            } finally {
                Logger.Message("In ProcessMessages/Finally");
                Disconnect();
            }
        }

        public void Disconnect() {
            _isProcessingMessages.Reset();
            lock (this) {
                _connectingTask = null;

                try {
                    if (_pipe != null) {
                        // ensure all queues are stopped and cleared out.
                        ManualEventQueue.ResetAllQueues();
                        _isBufferReady.Set();
                        var pipe = _pipe;
                        _pipe = null;
                        pipe.Close();
                        pipe.Dispose();
                    }
                } catch {
                    // just close it!
                }
            }
        }

        /// <summary>
        ///   Writes the message to the stream asyncly.
        /// </summary>
        /// <param name="message"> The request. </param>
        /// <returns> </returns>
        /// <remarks>
        /// </remarks>
        private static void WriteAsync(UrlEncodedMessage message) {
            if (IsPipeConnected) {
                try {
                    message.Add("rqid", Event<GetCurrentRequestId>.RaiseFirst());
                    _instance._pipe.WriteLineAsync(message.ToString()).ContinueWith(antecedent => Logger.Error("Async Write Fail!? (1)"),
                        TaskContinuationOptions.OnlyOnFaulted);
                } catch /* (Exception e) */ {
                }
            }
        }

        private void StartSession(string clientId, string sessionId) {
            WriteAsync(new UrlEncodedMessage("StartSession") {
                {"client", clientId},
                {"id", sessionId},
                {"rqid", sessionId},
            });
        }
    }
}