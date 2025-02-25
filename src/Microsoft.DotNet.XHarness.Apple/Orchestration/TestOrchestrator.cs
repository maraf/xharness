﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.XHarness.Common;
using Microsoft.DotNet.XHarness.Common.CLI;
using Microsoft.DotNet.XHarness.Common.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared;
using Microsoft.DotNet.XHarness.iOS.Shared.Hardware;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared.Utilities;

namespace Microsoft.DotNet.XHarness.Apple
{
    public interface ITestOrchestrator
    {
        Task<ExitCode> OrchestrateTest(
            AppBundleInformation appBundleInformation,
            TestTargetOs target,
            string? deviceName,
            TimeSpan timeout,
            TimeSpan launchTimeout,
            CommunicationChannel communicationChannel,
            XmlResultJargon xmlResultJargon,
            IEnumerable<string> singleMethodFilters,
            IEnumerable<string> classMethodFilters,
            bool includeWirelessDevices,
            bool resetSimulator,
            bool enableLldb,
            bool signalAppEnd,
            IReadOnlyCollection<(string, string)> environmentalVariables,
            IEnumerable<string> passthroughArguments,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Common ancestor for `test` and `just-test` orchestrators.
    /// </summary>
    public class TestOrchestrator : BaseOrchestrator, ITestOrchestrator
    {
        private readonly IAppTesterFactory _appTesterFactory;
        private readonly ILogger _logger;
        private readonly ILogs _logs;
        private readonly IFileBackedLog _mainLog;
        private readonly IErrorKnowledgeBase _errorKnowledgeBase;

        public TestOrchestrator(
            IAppInstaller appInstaller,
            IAppUninstaller appUninstaller,
            IAppTesterFactory appTesterFactory,
            IDeviceFinder deviceFinder,
            ILogger consoleLogger,
            ILogs logs,
            IFileBackedLog mainLog,
            IErrorKnowledgeBase errorKnowledgeBase,
            IDiagnosticsData diagnosticsData,
            IHelpers helpers)
            : base(appInstaller, appUninstaller, deviceFinder, consoleLogger, logs, mainLog, errorKnowledgeBase, diagnosticsData, helpers)
        {
            _appTesterFactory = appTesterFactory ?? throw new ArgumentNullException(nameof(appTesterFactory));
            _logger = consoleLogger ?? throw new ArgumentNullException(nameof(consoleLogger));
            _logs = logs ?? throw new ArgumentNullException(nameof(logs));
            _mainLog = mainLog ?? throw new ArgumentNullException(nameof(mainLog));
            _errorKnowledgeBase = errorKnowledgeBase ?? throw new ArgumentNullException(nameof(errorKnowledgeBase));
        }

        public virtual async Task<ExitCode> OrchestrateTest(
            AppBundleInformation appBundleInformation,
            TestTargetOs target,
            string? deviceName,
            TimeSpan timeout,
            TimeSpan launchTimeout,
            CommunicationChannel communicationChannel,
            XmlResultJargon xmlResultJargon,
            IEnumerable<string> singleMethodFilters,
            IEnumerable<string> classMethodFilters,
            bool includeWirelessDevices,
            bool resetSimulator,
            bool enableLldb,
            bool signalAppEnd,
            IReadOnlyCollection<(string, string)> environmentalVariables,
            IEnumerable<string> passthroughArguments,
            CancellationToken cancellationToken)
        {
            // The --launch-timeout option must start counting now and not complete before we start running tests to succeed.
            // After then, this timeout must not interfere.
            // Tests start running inside of ExecuteApp() which means we have to time-constrain all operations happening inside
            // OrchestrateRun() that happen before we start the app execution.
            // We will achieve this by sending a special cancellation token to OrchestrateRun() and only cancel if it in case
            // we didn't manage to start the app run until then.
            using var launchTimeoutCancellation = new CancellationTokenSource();
            var appRunStarted = false;
            var task = Task.Delay(launchTimeout < timeout ? launchTimeout : timeout).ContinueWith(t =>
            {
                if (!appRunStarted)
                {
                    launchTimeoutCancellation.Cancel();
                }
            });

            // We also want to shrink the launch timeout by whatever elapsed before we get to ExecuteApp
            var watch = Stopwatch.StartNew();

            using var launchTimeoutCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                launchTimeoutCancellation.Token,
                cancellationToken);

            Task<ExitCode> executeMacCatalystApp(AppBundleInformation appBundleInfo)
            {
                appRunStarted = true;
                return ExecuteMacCatalystApp(
                    appBundleInfo,
                    timeout,
                    launchTimeout - watch.Elapsed,
                    communicationChannel,
                    xmlResultJargon,
                    singleMethodFilters,
                    classMethodFilters,
                    environmentalVariables,
                    passthroughArguments,
                    signalAppEnd,
                    cancellationToken);
            }

            Task<ExitCode> executeApp(AppBundleInformation appBundleInfo, IDevice device, IDevice? companionDevice)
            {
                appRunStarted = true;
                return ExecuteApp(
                    appBundleInfo,
                    target,
                    device,
                    companionDevice,
                    timeout,
                    launchTimeout - watch.Elapsed,
                    communicationChannel,
                    xmlResultJargon,
                    singleMethodFilters,
                    classMethodFilters,
                    environmentalVariables,
                    passthroughArguments,
                    signalAppEnd,
                    cancellationToken);
            }

            return await OrchestrateOperation(
                target,
                deviceName,
                includeWirelessDevices,
                resetSimulator,
                enableLldb,
                appBundleInformation,
                executeMacCatalystApp,
                executeApp,
                launchTimeoutCancellationToken.Token);
        }

        private async Task<ExitCode> ExecuteApp(
            AppBundleInformation appBundleInfo,
            TestTargetOs target,
            IDevice device,
            IDevice? companionDevice,
            TimeSpan timeout,
            TimeSpan launchTimeout,
            CommunicationChannel communicationChannel,
            XmlResultJargon xmlResultJargon,
            IEnumerable<string> singleMethodFilters,
            IEnumerable<string> classMethodFilters,
            IReadOnlyCollection<(string, string)> environmentalVariables,
            IEnumerable<string> passthroughArguments,
            bool signalAppEnd,
            CancellationToken cancellationToken)
        {
            var runMode = target.Platform.ToRunMode();

            // iOS 14+ devices do not allow local network access and won't work unless the user confirms a dialog on the screen
            // https://developer.apple.com/forums/thread/663858
            if (Version.TryParse(device.OSVersion, out var version) && version.Major >= 14 && runMode == RunMode.iOS && communicationChannel == CommunicationChannel.Network)
            {
                _logger.LogWarning(
                    "Applications need user permission for communication over local network on iOS 14 and newer." + Environment.NewLine +
                    "Either confirm a dialog on the device after the application launches or use the USB tunnel communication channel." + Environment.NewLine +
                    "Test run might fail if permission is not granted. Permission is valid until app is uninstalled.");
            }

            if (signalAppEnd && (runMode == RunMode.Sim64 || runMode == RunMode.Sim32))
            {
                _logger.LogWarning("The --signal-app-end option is used for device tests and has no effect on simulators");
            }

            _logger.LogInformation("Starting test run for " + appBundleInfo.BundleIdentifier + "..");

            var appTester = GetAppTester(communicationChannel, target.Platform.IsSimulator());

            (TestExecutingResult testResult, string resultMessage) = await appTester.TestApp(
                appBundleInfo,
                target,
                device,
                companionDevice,
                timeout,
                launchTimeout,
                signalAppEnd,
                passthroughArguments,
                environmentalVariables,
                xmlResultJargon,
                skippedMethods: singleMethodFilters?.ToArray(),
                skippedTestClasses: classMethodFilters?.ToArray(),
                cancellationToken: cancellationToken);

            return ParseResult(testResult, resultMessage);
        }

        private async Task<ExitCode> ExecuteMacCatalystApp(
            AppBundleInformation appBundleInfo,
            TimeSpan timeout,
            TimeSpan launchTimeout,
            CommunicationChannel communicationChannel,
            XmlResultJargon xmlResultJargon,
            IEnumerable<string> singleMethodFilters,
            IEnumerable<string> classMethodFilters,
            IReadOnlyCollection<(string, string)> environmentalVariables,
            IEnumerable<string> passthroughArguments,
            bool signalAppEnd,
            CancellationToken cancellationToken)
        {
            var appTester = GetAppTester(communicationChannel, TestTarget.MacCatalyst.IsSimulator());

            (TestExecutingResult testResult, string resultMessage) = await appTester.TestMacCatalystApp(
                appBundleInfo,
                timeout,
                launchTimeout,
                signalAppEnd,
                passthroughArguments,
                environmentalVariables,
                xmlResultJargon,
                skippedMethods: singleMethodFilters?.ToArray(),
                skippedTestClasses: classMethodFilters?.ToArray(),
                cancellationToken: cancellationToken);

            return ParseResult(testResult, resultMessage);
        }

        private IAppTester GetAppTester(CommunicationChannel communicationChannel, bool isSimulator)
        {
            // Only add the extra callback if we do know that the feature was indeed enabled
            Action<string>? logCallback = IsLldbEnabled() ? (l) => NotifyUserLldbCommand(_logger, l) : null;

            return _appTesterFactory.Create(communicationChannel, isSimulator, _mainLog, _logs, logCallback);
        }

        private ExitCode ParseResult(TestExecutingResult testResult, string resultMessage)
        {
            string newLine = Environment.NewLine;
            const string checkLogsMessage = "Check logs for more information";

            ExitCode LogProblem(string message, ExitCode defaultExitCode)
            {
                foreach (var log in _logs)
                {
                    if (_errorKnowledgeBase.IsKnownTestIssue(log, out var issue))
                    {
                        _logger.LogError(message + newLine + issue.HumanMessage);
                        return issue.SuggestedExitCode.HasValue ? (ExitCode)issue.SuggestedExitCode.Value : defaultExitCode;
                    }
                }

                if (resultMessage != null)
                {
                    _logger.LogError(message + newLine + resultMessage + newLine + newLine + checkLogsMessage);
                }
                else
                {
                    _logger.LogError(message + newLine + checkLogsMessage);
                }

                return defaultExitCode;
            }

            switch (testResult)
            {
                case TestExecutingResult.Succeeded:
                    _logger.LogInformation($"Application finished the test run successfully");
                    _logger.LogInformation(resultMessage);
                    return ExitCode.SUCCESS;

                case TestExecutingResult.Failed:
                    _logger.LogInformation($"Application finished the test run successfully with some failed tests");
                    _logger.LogInformation(resultMessage);
                    return ExitCode.TESTS_FAILED;

                case TestExecutingResult.LaunchFailure:
                    return LogProblem("Failed to launch the application", ExitCode.APP_LAUNCH_FAILURE);

                case TestExecutingResult.Crashed:
                    return LogProblem("Application test run crashed", ExitCode.APP_LAUNCH_FAILURE);

                case TestExecutingResult.TimedOut:
                    _logger.LogWarning($"Application test run timed out");
                    return ExitCode.TIMED_OUT;

                default:
                    _logger.LogError($"Application test run ended in an unexpected way: '{testResult}'" +
                        newLine + (resultMessage != null ? resultMessage + newLine + newLine : null) + checkLogsMessage);
                    return ExitCode.GENERAL_FAILURE;
            }
        }
    }
}
