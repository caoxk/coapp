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
    using System.IO;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Net;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using Common;
    using Common.Exceptions;
    using Common.Model.Atom;
    using Toolkit.Extensions;
    using Toolkit.Linq;
    using Toolkit.Logging;
    using Toolkit.Pipes;
    using Toolkit.TaskService;
    using Toolkit.Tasks;
    using Toolkit.Win32;
    using PkgFilter = System.Linq.Expressions.Expression<System.Func<Common.IPackage, bool>>;
    using CollectionFilter = Toolkit.Collections.XList<System.Linq.Expressions.Expression<System.Func<System.Collections.Generic.IEnumerable<Common.IPackage>,System.Collections.Generic.IEnumerable<Common.IPackage>>>>;
    using Task = System.Threading.Tasks.Task;
    using System.Text;

    public class GeneralPackageInformation {
        public int Priority { get; set; }
        public CanonicalName CanonicalName { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public class PackageManager {
        private static readonly IPackageManager Remote = Session.RemoteService;
        internal static PackageManager Instance;
        
        public PackageManager() {
            // store the current instance of the PM.\
            Instance = this;
        }
        static PackageManager() {
            // Serializer for System.Linq.Expressions.Expression
            CustomSerializer.Add(new CustomSerializer<Expression<Func<IPackage, bool>>>((message, key, serialize) => message.Add(key, CustomSerializer.ExpressionXmlSerializer.Serialize(serialize).ToString(SaveOptions.OmitDuplicateNamespaces | SaveOptions.DisableFormatting)), (message, key) => (Expression<Func<IPackage, bool>>)CustomSerializer.ExpressionXmlSerializer.Deserialize(message[key])));
            CustomSerializer.Add(new CustomSerializer<Expression<Func<IEnumerable<IPackage>, IEnumerable<IPackage>>>>((message, key, serialize) => message.Add(key, CustomSerializer.ExpressionXmlSerializer.Serialize(serialize).ToString(SaveOptions.OmitDuplicateNamespaces | SaveOptions.DisableFormatting)), (message, key) => (Expression<Func<IEnumerable<IPackage>, IEnumerable<IPackage>>>)CustomSerializer.ExpressionXmlSerializer.Deserialize(message[key])));
        }

        public Task<IEnumerable<Package>> QueryPackages(string query, PkgFilter pkgFilter, CollectionFilter collectionFilter ,string location) {
            string localPath = null;

            // there are three possibilities:
            // 1. The parameter is a file on disk, or a remote file.
            try {
                // some kind of local file?
                if( File.Exists(query) ) {
                    return PackagesFromLocalFile(query.EnsureFileIsLocal(), Path.GetDirectoryName(query.GetFullPath()),pkgFilter,collectionFilter);
                }
                
                var uri = new Uri(query);
                if (uri.IsWebUri()) {
                    Task.Factory.StartNew(() => {
                        var lp = uri.AbsolutePath.MakeSafeFileName().GenerateTemporaryFilename();
                        var remoteFile = new RemoteFile(uri, lp, itemUri => {
                            Event<DownloadCompleted>.Raise(uri.AbsolutePath, lp); // got the file!
                            localPath = lp;
                        },
                            itemUri => {
                                localPath = null; // failed
                            },
                            (itemUri, percent) => Event<DownloadProgress>.Raise(uri.AbsolutePath, localPath, percent));
                        remoteFile.Get();

                        if (localPath != null) {
                            return PackagesFromLocalFile(localPath, null, pkgFilter, collectionFilter).Result;
                        }
                        return Enumerable.Empty<Package>();
                    });
                }
            }
            catch {
                // ignore what can't be fixed
            }

            // 2. A directory path or filemask (ie, creating a one-time 'feed' )
            if (Directory.Exists(query) || query.IndexOf('\\') > -1 || query.IndexOf('/') > -1 || (query.IndexOf('*') > -1 && query.ToLower().EndsWith(".msi"))) {
                // specified a folder, or some kind of path that looks like a feed.
                // add it as a feed, and then get the contents of that feed.
                return (Remote.AddFeed(query, true) as Task<PackageManagerResponseImpl>).Continue(response => {
                    // a feed!
                    if (response.Feeds.Any()) {
                        return (Remote.FindPackages(null, pkgFilter,collectionFilter, response.Feeds.First().Location) as Task<PackageManagerResponseImpl>).Continue(response1 =>response1.Packages).Result;
                    }

                    query = query.GetFullPath();
                    return (Remote.AddFeed(query, true) as Task<PackageManagerResponseImpl>).Continue(response2 => {
                        if (response.Feeds.Any()) {
                            return (Remote.FindPackages(null, pkgFilter, collectionFilter, response2.Feeds.First().Location) as Task<PackageManagerResponseImpl>).Continue( response1 => response1.Packages).Result;
                        }
                        return Enumerable.Empty<Package>();
                    }).Result;
                });
            }

            // 3. A partial/canonical name of a package.
            return (Remote.FindPackages(query, pkgFilter,collectionFilter, location) as Task<PackageManagerResponseImpl>).Continue(response => response.Packages);
        }

        public Task<IEnumerable<Package>> QueryPackages(IEnumerable<string> queries, PkgFilter pkgFilter, CollectionFilter collectionFilter, string location) {
            if( queries != null && queries.Any()) {
                queries = queries.Distinct().ToArray();
                return queries.Select(each => QueryPackages(each, pkgFilter, collectionFilter, location)).Continue(results => results.SelectMany(result => result).Distinct());    
            }
            return QueryPackages("*", pkgFilter,collectionFilter, location);
        }

        private Task<IEnumerable<Package>> PackagesFromLocalFile(string localPath, string originalDirectory, PkgFilter pkgFilter,CollectionFilter collectionFilter) {
            if( localPath.FileIsLocalAndExists()) {
                return (Remote.RecognizeFile("", localPath, "") as Task<PackageManagerResponseImpl>).Continue(response => {
                    // was that one or more package(s)?
                    if (response.Packages.Any()) {
                        if (!string.IsNullOrEmpty(originalDirectory)) {
                            Remote.AddFeed(originalDirectory, true);
                        }
                        return response.Packages;
                    }

                    // was that a feed?
                    if (response.Feeds.Any()) {
                        return (Remote.FindPackages(null, pkgFilter,collectionFilter, response.Feeds.First().Location) as Task<PackageManagerResponseImpl>).Continue(response2 => response2.Packages).Result;
                    }

                    // nothing we could recognize.
                    return Enumerable.Empty<Package>();
                });
            }
            return Enumerable.Empty<Package>().AsResultTask();
        }

        /// <summary>
        ///   Returns a collection of packages that are filtered to the platform switches passed on the command line.
        /// </summary>
        /// <param name="packages"> The collection to filter packages for </param>
        /// <param name="x86"> Accept x86 packages? </param>
        /// <param name="x64"> Accept x64 packages? </param>
        /// <param name="cpuany"> Accept CPUANY packages? </param>
        /// <returns> </returns>
        public IEnumerable<Package> FilterPackagesForPlatforms(IEnumerable<Package> packages, bool? x86 = null, bool? x64 = null, bool? cpuany = null) {
            if ((true == x64) || (true == x86) || (true == cpuany)) {
                return packages.Where(each => (x64 == true && each.Architecture == Architecture.x64) || (x86 == true && each.Architecture == Architecture.x86) || (cpuany == true && each.Architecture == Architecture.Any));
            }
            return packages;
        }

        /// <summary>
        ///   makes sure that there isn't a conflict in the packages that are being installed. This will call FilterPackagesForPlatforms, so you don't need to call that beforehand.
        /// </summary>
        /// <param name="packages"> the collection of packages to check for conflicts </param>
        /// <param name="x86"> </param>
        /// <param name="x64"> </param>
        /// <param name="cpuany"> </param>
        /// <returns> A filtered collection of packages to install </returns>
        public Task<IEnumerable<Package>> FilterConflictsForInstall(IEnumerable<Package> packages, bool? x86 = null, bool? x64 = null, bool? cpuany = null) {
            return Task.Factory.StartNew(() => {
                var isFiltering = (true == x64) || (true == x86) || (true == cpuany);

                var pkgs = FilterPackagesForPlatforms(packages, x86, x64, cpuany).Distinct().ToArray();

                // get an collection for each package of all the other packages that it matches.
                var packageFamilies = pkgs.Select(package => pkgs.Where(each => each.Name == package.Name && each.PublicKeyToken == package.PublicKeyToken && (!isFiltering || each.Architecture == package.Architecture)).ToArray()).ToArray();

                // get the packages that could be conflicting.
                var conflictedFamilies = packageFamilies.Where(eachFamily => eachFamily.Count() > 1);

                if (conflictedFamilies.Any()) {
                    var nonConflictedPackages = packageFamilies.Where(eachFamily => eachFamily.Count() == 1).Select(eachFamily => eachFamily.FirstOrDefault());

                    if (!isFiltering) {
                        var actualConflicts = new List<Package[]>();
                        foreach (var conflictedPackages in conflictedFamilies) {
                            // we're really only interested in one platform for a given package here (since the user didn't specify any preference directly)

                            if (Environment.Is64BitOperatingSystem) {
                                // if there is a single x64 package, take that.
                                var x64Pkgs = conflictedPackages.Where(each => each.Architecture == Architecture.x64).ToArray();
                                if (x64Pkgs.Count() == 1) {
                                    nonConflictedPackages = nonConflictedPackages.Union(x64Pkgs);
                                    continue;
                                }

                                if (x64Pkgs.Count() > 1) {
                                    // we've got more than one package of a single platform. just add it to the conflicts and move along.
                                    actualConflicts.Add(conflictedPackages);
                                    continue;
                                }
                            }

                            // if there are no x64 packages, see if there is a single x86 package, take that.
                            var x86Pkgs = conflictedPackages.Where(each => each.Architecture == Architecture.x86).ToArray();
                            if (x86Pkgs.Length == 1) {
                                nonConflictedPackages = nonConflictedPackages.Union(x86Pkgs);
                                continue;
                            }

                            // if we got here, that means no x64  and no x86 packages, and we should just have a plurality of cpuany packages.
                            // we've got more than one package of a single platform, or that means no x64  and no x86 packages, 
                            // and we just have a plurality of cpuany packages.

                            // Either way just add it to the conflicts and move along.
                            actualConflicts.Add(conflictedPackages);
                        }
                        if (actualConflicts.Any()) {
                            throw new ConflictedPackagesException(actualConflicts);
                        }

                        // hmm, we resolved our conflicts automagically.
                        return nonConflictedPackages;
                    }
                    // architectures are not factored in here. 
                    // we must have multiple packages of a given architecture.
                    throw new ConflictedPackagesException(conflictedFamilies);
                }

                return pkgs;
            }, TaskCreationOptions.AttachedToParent);
        }

        /// <summary>
        ///   This identifies the actual list of packages to install for a given requested set of packages
        /// </summary>
        /// <param name="packages"> </param>
        /// <param name="autoUpgrade"> </param>
        /// <param name="download"> </param>
        /// <returns> </returns>
        public Task<IEnumerable<Package>> IdentifyPackageAndDependenciesToInstall(IEnumerable<Package> packages, bool? autoUpgrade = null, bool? download = null) {
            
            var pTasks = packages.Select(package => Install(package.CanonicalName, autoUpgrade, pretend: true, download: download));
            /*
            return pTasks.ContinueAlways(antecedents => {
                if (antecedents.Any(each => each.IsFaulted)) {
                    throw new AggregateException(antecedents.Where(each => each.IsFaulted).Select(each => each.Exception));
                }
                return  antecedents.SelectMany(each => each.Result);
                // pkgsToinstall => pkgsToinstall.SelectMany(each => antecedents.Result).Distinct();
            });
             * */
            return pTasks.Continue(pkgsToinstall => pkgsToinstall.SelectMany(each => each).Distinct());
        }

        public Task Elevate() {
            return Session.Elevate();
        }

        public Task<bool> VerifyFileSignature(string filename) {
            // make the remote call via the interface.
            return (Remote.VerifyFileSignature(filename) as Task<PackageManagerResponseImpl>).Continue(response => response.ValidationState[filename]);
        }

        public Task SetGeneralPackageInformation(int priority, CanonicalName canonicalName, string key, string value) {
            return Remote.SetGeneralPackageInformation(priority, canonicalName, key, value);
        }
        public Task<IEnumerable<GeneralPackageInformation>>  GeneralPackageInformation { get {
            return (Remote.GetGeneralPackageInformation() as Task<PackageManagerResponseImpl>).Continue(response => response.GeneralPackageInformation);
        }}

        public Task SetPackageWanted(CanonicalName canonicalName, bool wanted) {
            return Remote.SetPackageWanted(canonicalName, wanted);
        }

        private Package NewestCompatablePackageIn(Package aPackage, IEnumerable<Package> packages) {
            var result = aPackage;
            var pkgs = packages.OrderBy(each => each.Version).ToArray();
            Package pk;

            while ((pk = pkgs.FirstOrDefault( p => p.BindingPolicy.Minimum <= result.Version && p.BindingPolicy.Maximum >= result.Version && result.Version < p.Version)) != null) {
                result = pk;
            }
            return result;
        }

        public Task<IEnumerable<Package>> GetActiveVersion(CanonicalName packageName) {
            return FindPackages(packageName, Package.Properties.Active.Is(true));
        }
        public Task<IEnumerable<Package>> GetActiveVersions(IEnumerable<CanonicalName> packageNames) {
            return packageNames.Select(GetActiveVersion).Continue(results => results.SelectMany(result => result).Distinct());
        }

        public Task<IEnumerable<Package>> GetAllVersionsOfPackage(CanonicalName packageName) {
            return FindPackages(packageName.OtherVersionFilter);
        }
        
        public Task<IEnumerable<Package>> GetInstalledPackages(CanonicalName packageName, string locationFeed = null) {
            return FindPackages(packageName.OtherVersionFilter, Package.Properties.Installed.Is(true),locationFeed:locationFeed);
        }
        public Task<IEnumerable<Package>> GetInstalledPackages(IEnumerable<CanonicalName> packageNames, string locationFeed = null) {
            return packageNames.Select(each => GetInstalledPackages(each, locationFeed)).Continue(results => results.SelectMany(result => result).Distinct());
        }

        public Task<IEnumerable<Package>> FindPackages(CanonicalName packageName, PkgFilter pkgFilter = null, CollectionFilter collectionFilter = null, string locationFeed = null) {
            return (Remote.FindPackages(packageName, pkgFilter, collectionFilter , locationFeed) as Task<PackageManagerResponseImpl>).Continue(response => response.Packages);
        }

        public Task<Package> GetPackageFromFile(string filename) {
            filename = filename.GetFullPath();
            if( File.Exists(filename)) {
                return QueryPackages(filename,null,null,null).Continue(packages => {
                    var pkg = packages.FirstOrDefault();
                    if (pkg == null) {
                        throw new UnknownPackageException("filename: {0}".format(filename));
                    }
                    return pkg;
                });    
            }
            return ((Package)null).AsCanceledTask();
        }

        private Task<TReturnType> InvalidCanonicalNameResult<TReturnType>(CanonicalName canonicalName) {
            var failedResult = new TaskCompletionSource<TReturnType>();
            failedResult.SetException(new InvalidCanonicalNameException(canonicalName));
            return failedResult.Task;
        }

        public Task<IEnumerable<Package>> Install(CanonicalName canonicalName, bool? autoUpgrade = null, bool? force = null, bool? download = null, bool? pretend = null, CanonicalName replacingPackage = null) {
            if (!canonicalName.IsCanonical) {
                return InvalidCanonicalNameResult<IEnumerable<Package>>(canonicalName);
            }

            var completedThisPackage = false;

            CurrentTask.Events += new PackageInstallProgress((name, progress, overallProgress) => {
                if (overallProgress == 100) {
                    completedThisPackage = true;
                }
            });
            GetTelemetry(); // ensure this value is cached before the install.

            return (Remote.InstallPackage(canonicalName, autoUpgrade, force, download, pretend, replacingPackage) as Task<PackageManagerResponseImpl>).Continue(response => {
                if (response.PotentialUpgrades != null) {
                    throw new PackageHasPotentialUpgradesException(response.UpgradablePackage, response.PotentialUpgrades);
                }
                return response.Packages;
            });
        }

        public Task<IEnumerable<Package>> WhatWouldBeInstalled(CanonicalName canonicalName, bool? autoUpgrade = null) {
            return Install(canonicalName, autoUpgrade, false, false, false, null);
        }

        public Task<int> RemovePackages(IEnumerable<CanonicalName> canonicalNames, bool forceRemoval) {
            var packagesToRemove = canonicalNames.ToList();

            return Task.Factory.StartNew(() => {
                int[] total = {0};
                int packageCount;

                // this will keep looping around as long as the last pass removed a package
                // it was way easier to do this than to figure out what the order of removal is. :P
                while ((packageCount = packagesToRemove.Count) > 0) {
                    var packageFailures = new List<Exception>();

                    foreach (var cn in packagesToRemove.ToArray()) {
                        var canonicalName = cn;
                        try {
                            // remove the package and wait for completion.
                            RemovePackage(canonicalName, forceRemoval).Wait();

                            packagesToRemove.Remove(canonicalName);
                            total[0] = total[0] + 1;
                        } catch (Exception exception) {
                            packageFailures.Add(exception);
                        }
                    }

                    if (packagesToRemove.Any() && packageCount == packagesToRemove.Count) {
                        // it didn't remove any on that pass. we're boned.
                        throw new AggregateException(packageFailures);
                    }
                }
                return total[0];
            }, TaskCreationOptions.AttachedToParent);
        }

        public Task RemovePackage(CanonicalName canonicalName, bool forceRemoval) {
            if (!canonicalName.IsCanonical) {
                return InvalidCanonicalNameResult<IEnumerable<Package>>(canonicalName);
            }

            return Remote.RemovePackage(canonicalName, forceRemoval);
        }

        private Task<LoggingSettings> SetLogging(bool? messages = null, bool? warnings = null, bool? errors = null) {
            return (Remote.SetLogging(messages, warnings, errors) as Task<PackageManagerResponseImpl>).Continue(response => response.LoggingSettingsResult);
        }

        public Task EnableMessageLogging() {
            Logger.Messages = true;
            return SetLogging(messages: true);
        }

        public Task DisableMessageLogging() {
            Logger.Messages = false;
            return SetLogging(messages: false);
        }

        public Task<bool> IsMessageLogging {
            get {
                return SetLogging().Continue(results => results.Messages);
            }
        }

        public Task EnableWarningLogging() {
            Logger.Warnings = true;
            return SetLogging(warnings: true);
        }

        public Task DisableWarningLogging() {
            Logger.Warnings = false;
            return SetLogging(messages: false);
        }

        public Task<bool> IsWarningLogging {
            get {
                return SetLogging().Continue(results => results.Warnings);
            }
        }

        public Task EnableErrorLogging() {
            Logger.Errors = true;
            return SetLogging(errors: true);
        }

        public Task DisableErrorLogging() {
            Logger.Errors = false;
            return SetLogging(errors: false);
        }

        public Task<bool> IsErrorLogging {
            get {
                return SetLogging().Continue(results => results.Errors);
            }
        }

        public Task<string> RemoveSystemFeed(string feedLocation) {
            return (Remote.RemoveFeed(feedLocation, false) as Task<PackageManagerResponseImpl>).Continue(response => feedLocation);
        }

        public Task<string> RemoveSessionFeed(string feedLocation) {
            return (Remote.RemoveFeed(feedLocation, true) as Task<PackageManagerResponseImpl>).Continue(response => feedLocation);
        }

        public Task<string> AddSystemFeed(string feedLocation) {
            return (Remote.AddFeed(feedLocation, false) as Task<PackageManagerResponseImpl>).Continue(response => response.Feeds.Select(each => each.Location).FirstOrDefault());
        }

        public Task<string> AddSessionFeed(string feedLocation) {
            return (Remote.AddFeed(feedLocation, true) as Task<PackageManagerResponseImpl>).Continue(response => response.Feeds.Select(each => each.Location).FirstOrDefault());
        }

        public Task SuppressFeed(string feedLocation) {
            return (Remote.SuppressFeed(feedLocation));
        }

        public Task SetFeed(string feedLocation, FeedState state) {
            return Remote.SetFeedFlags(feedLocation, state);
        }

        public Task<IEnumerable<Feed>> Feeds {
            get {
                return (Remote.ListFeeds() as Task<PackageManagerResponseImpl>).Continue(response => response.Feeds);
            }
        }

        public Task SetFeedStale(string feedLocation) {
            return Remote.SetFeedStale(feedLocation);
        }

        public Task SetAllFeedsStale() {
            return Feeds.Continue(feeds => {
                foreach (var feed in feeds) {
                    SetFeedStale(feed.Location).RethrowWhenFaulted();
                }
            });
        }

        public Task<Package> GetPackage(CanonicalName canonicalName, bool forceRefresh = false) {
            if( null == canonicalName) {
                return CoTask.AsResultTask<Package>(null);
            }
            if (!canonicalName.IsCanonical) {
                return InvalidCanonicalNameResult<Package>(canonicalName);
            }

            var pkg = Package.GetPackage(canonicalName);

            if (forceRefresh || pkg.IsPackageInfoStale) {
                return (Remote.FindPackages(canonicalName,null,null,null) as Task<PackageManagerResponseImpl>).Continue(response => pkg);
            }

            return pkg.AsResultTask();
        }

        public Task<IEnumerable<Package>> GetPackages(IEnumerable<CanonicalName> canonicalNames, bool forceRefresh = false) {
            if(canonicalNames.IsNullOrEmpty()) {
                return Enumerable.Empty<Package>().AsResultTask();
            }
            return canonicalNames.Select(c => GetPackage(c, true)).Continue(all => all);
        }

        public Task<Package> GetPackageDetails(CanonicalName canonicalName, bool forceRefresh = false) {
            return Package.GetPackage(canonicalName).AsResultTask();
            /*
            if (!canonicalName.IsCanonical) {
                return InvalidCanonicalNameResult<Package>(canonicalName);
            }

            return GetPackage(canonicalName, forceRefresh).Continue(pkg => {
                if (forceRefresh || pkg.IsPackageDetailsStale) {
                    Remote.GetPackageDetails(canonicalName).Wait();
                }
                return pkg;
            });
             * */
        }

        private bool? _telemetry;
        public bool Telemetry { get {
            if( !_telemetry.HasValue ) {
                GetTelemetry().Wait();
            }
            return true == _telemetry;
        }}

        public Task<bool> GetTelemetry() {
            return (Remote.GetTelemetry() as Task<PackageManagerResponseImpl>).Continue(response => true == (_telemetry = response.OptedIn));
        }

        public Task SetTelemetry(bool optInToTelemetry) {
            return Remote.SetTelemetry(optInToTelemetry);
        }

        public Task CreateSymlink(string existingLocation, string newLink) {
            return Remote.CreateSymlink(existingLocation, newLink, LinkType.Symlink);
        }

        public Task CreateHardlink(string existingLocation, string newLink) {
            return Remote.CreateSymlink(existingLocation, newLink, LinkType.Hardlink);
        }

        public Task CreateShortcut(string existingLocation, string newLink) {
            return Remote.CreateSymlink(existingLocation, newLink, LinkType.Shortcut);
        }

        public Task RemoveFromPolicy(string policyName, string account) {
            return Remote.RemoveFromPolicy(policyName, account);
        }

        public Task AddToPolicy(string policyName, string account) {
            return Remote.AddToPolicy(policyName, account);
        }

        public Task<Policy> GetPolicy(string policyName) {
            return (Remote.GetPolicy(policyName) as Task<PackageManagerResponseImpl>).Continue(response => response.Policies.FirstOrDefault());
        }

        public Task<IEnumerable<Policy>> Policies {
            get {
                return (Remote.GetPolicy("*") as Task<PackageManagerResponseImpl>).Continue(response => response.Policies);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="taskName"> the name of the task. If a task with this name already exists, it will be overwritten. </param>
        /// <param name="executable"> </param>
        /// <param name="commandline"> </param>
        /// <param name="hour"> </param>
        /// <param name="minutes"> </param>
        /// <param name="dayOfWeek"> </param>
        /// <param name="intervalInMinutes"> how often the scheduled task should consider running (on Windows XP/2003, it's not possible to run as soon as possible after a task was missed. </param>
        /// <returns> </returns>
        public Task AddScheduledTask(string taskName, string executable, string commandline, int hour, int minutes, DayOfWeek? dayOfWeek, int intervalInMinutes) {
            // remote version doesn't work without user credentials. #sigh

            // return Remote.ScheduleTask(taskName, executable, commandline, hour, minutes, dayOfWeek, intervalInMinutes);
            return RemoveScheduledTask(taskName).Continue(() => {
                var dow = DaysOfTheWeek.AllDays;

                if (dayOfWeek.HasValue) {
                    // once-a-week
                    switch (dayOfWeek.Value) {
                        case DayOfWeek.Saturday:
                            dow = DaysOfTheWeek.Saturday;
                            break;
                        case DayOfWeek.Sunday:
                            dow = DaysOfTheWeek.Sunday;
                            break;
                        case DayOfWeek.Monday:
                            dow = DaysOfTheWeek.Monday;
                            break;
                        case DayOfWeek.Tuesday:
                            dow = DaysOfTheWeek.Tuesday;
                            break;
                        case DayOfWeek.Wednesday:
                            dow = DaysOfTheWeek.Wednesday;
                            break;
                        case DayOfWeek.Thursday:
                            dow = DaysOfTheWeek.Thursday;
                            break;
                        case DayOfWeek.Friday:
                            dow = DaysOfTheWeek.Friday;
                            break;
                    }
                }
                using (var taskService = new TaskService()) {
                    var tName = "coapp\\" + taskName;
                    if (taskService.HighestSupportedVersion < new Version(1, 2)) {
                        // old style
                        tName = "coapp-" + taskName;
                    }

                    var trigger = Trigger.CreateTrigger(TaskTriggerType.Weekly) as WeeklyTrigger;
                    trigger.DaysOfWeek = dow;
                    if (intervalInMinutes != 0 && intervalInMinutes < 1440) {
                        trigger.Repetition.Interval = new TimeSpan(0, intervalInMinutes, 0);
                    }

                    trigger.StartBoundary = DateTime.Today + new TimeSpan(hour, minutes, 0);

                    var t = taskService.AddTask(tName, trigger, new ExecAction(executable, commandline));
                    t.Definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                    t.Definition.Settings.RunOnlyIfNetworkAvailable = true;
                    t.RegisterChanges();
                }
            });

        }

        public Task RemoveScheduledTask(string taskName) {
            return Remote.RemoveScheduledTask(taskName);
        }

        public Task<ScheduledTask> GetScheduledTask(string taskName) {
            return (Remote.GetScheduledTasks(taskName) as Task<PackageManagerResponseImpl>).Continue(response => response.ScheduledTasks.FirstOrDefault());
        }

        public Task<IEnumerable<ScheduledTask>> ScheduledTasks {
            get {
                return (Remote.GetScheduledTasks("*") as Task<PackageManagerResponseImpl>).Continue(response => response.ScheduledTasks);
            }
        }

        public Task<IEnumerable<string>> TrustedPublishers {
            get {
                return (Remote.GetTrustedPublishers() as Task<PackageManagerResponseImpl>).Continue(response => response.Publishers);
            }
        }

        public Task AddTrustedPublisher(string publicKeyToken) {
            return Remote.AddTrustedPublisher(publicKeyToken);
        }

        public Task RemoveTrustedPublisher(string publicKeyToken) {
            return Remote.RemoveTrustedPublisher(publicKeyToken);
        }

        // GS01: TrustedPublishers Coming Soon.

        public Task<IEnumerable<Package>> RecognizeFile(string filename) {
            return (Remote.RecognizeFile(null, filename, null) as Task<PackageManagerResponseImpl>).Continue( response => response.Packages);
        }

        public Task<IEnumerable<Package>> RecognizeFiles(IEnumerable<string> filenames) {
            return (Remote.RecognizeFiles(filenames) as Task<PackageManagerResponseImpl>).Continue( response => response.Packages );
        }

        public Task<AtomItem> GetAtomItem(CanonicalName canonicalName) {
            return GetAtomFeed(canonicalName.SingleItemAsEnumerable()).Continue( feed => feed.Items.FirstOrDefault() as AtomItem);
        }

        public Task<AtomFeed> GetAtomFeed(CanonicalName canonicalName) {
            return GetAtomFeed(canonicalName.SingleItemAsEnumerable());
        }

        public Task<AtomFeed> GetAtomFeed(IEnumerable<CanonicalName> canonicalNames) {
            return (Remote.GetAtomFeed(canonicalNames) as Task<PackageManagerResponseImpl>).Continue(response => response.Feed);   
        }

        public Task SetConfigurationValue(string key, string valuename, string value) {
            return Remote.SetConfigurationValue(key, valuename, value);
        }

        public Task<string> GetConfigurationValue(string key, string valuename) {
            return (Remote.GetConfigurationValue(key, valuename) as Task<PackageManagerResponseImpl>).Continue(response => response.ResultString);   
        }

        public string GetEventLog(TimeSpan howMuch, TimeSpan startHowFarBack) {
            return Logger.GetMessages(howMuch, startHowFarBack);
        }
        public string GetEventLog(TimeSpan howMuch) {
            return GetEventLog(howMuch, new TimeSpan(0));
        }
        public string GetEventLog() {
            return GetEventLog(new TimeSpan(0, 5, 0), new TimeSpan(0));
        }

        public Task<string> UploadDebugInformation(string textContent = null) {
            textContent = textContent ?? GetEventLog();

            return Task.Factory.StartNew(() => {
                    // ping the coapp server to tell it that a package installed
                    try {
                        var hash = DateTime.Now.Ticks.ToString().MD5Hash();
                        var req = HttpWebRequest.Create("http://coapp.org/debug/");
                        req.Method = "POST";
                        UrlEncodedMessage uem = new UrlEncodedMessage();
                        uem.Add("uniqId", AnonymousId);
                        uem.Add("hash",hash);
                        uem.Add("content", textContent);

                        var buffer = new ASCIIEncoding().GetBytes(uem.ToString());
                        req.ContentType = "application/x-www-form-urlencoded";
                        req.ContentLength = buffer.Length;

                        using( var newStream = req.GetRequestStream() ) {
                            newStream.Write(buffer, 0, buffer.Length);
                        }

                        req.BetterGetResponse().Close();
                        return hash;
                    }
                    catch {
                        // who cares...
                    }
                return null;
            }, TaskCreationOptions.AttachedToParent);
        }

        private string _anonymousId;

        public string AnonymousId {
            get {
                if (string.IsNullOrEmpty(_anonymousId)) {
                    _anonymousId = GetConfigurationValue("", "AnonymousId").Result;
                    if (string.IsNullOrEmpty(_anonymousId) || _anonymousId.Length != 32) {
                        _anonymousId = Guid.NewGuid().ToString("N");
                        SetConfigurationValue("", "AnonymousId", _anonymousId);
                    }
                }
                return _anonymousId;
            }
        }

        public Task AutoTrim(CanonicalName packageMask) {
            return Remote.AutoTrim(packageMask ?? CanonicalName.CoAppPackages);
        }
    }
}