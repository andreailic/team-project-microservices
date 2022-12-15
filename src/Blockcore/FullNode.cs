﻿using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Blockcore.AsyncWork;
using Blockcore.Base;
using Blockcore.Builder;
using Blockcore.Configuration;
using Blockcore.Connection;
using Blockcore.Consensus;
using Blockcore.Consensus.Chain;
using Blockcore.Interfaces;
using Blockcore.Networks;
using Blockcore.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blockcore
{
    /// <summary>
    /// Node providing all supported features of the blockchain and its network.
    /// </summary>
    public class FullNode : IFullNode
    {
        /// <summary>Instance logger.</summary>
        private ILogger logger;

        /// <summary>Factory for creating loggers.</summary>
        private ILoggerFactory loggerFactory; // kada izbrisem,pojavi se greska

        /// <summary>Component responsible for starting and stopping all the node's features.</summary>
        private FullNodeFeatureExecutor fullNodeFeatureExecutor;

        /// <summary>Node command line and configuration file settings.</summary>
        public NodeSettings Settings { get; private set; }

        /// <summary>Information about the best chain.</summary>
        public IChainState ChainBehaviorState { get; private set; }

        /// <summary>Provider of IBD state.</summary>
        public IInitialBlockDownloadState InitialBlockDownloadState { get; private set; }

        /// <summary>Provider of notification about newly available blocks and transactions.</summary>
        public Signals.ISignals Signals { get; set; }

        /// <inheritdoc />
        public FullNodeState State { get; private set; }

        /// <inheritdoc />
        public DateTime StartTime { get; set; }

        /// <summary>Component responsible for connections to peers in P2P network.</summary>
        public IConnectionManager ConnectionManager { get; set; }

        /// <summary>Best chain of block headers from genesis.</summary>
        public ChainIndexer ChainIndexer { get; set; }

        /// <summary>Factory for creating and execution of asynchronous loops.</summary>
        public IAsyncProvider AsyncProvider { get; set; }

        /// <summary>Specification of the network the node runs on - regtest/testnet/mainnet.</summary>
        public Network Network { get; internal set; }

        /// <summary>Contains path locations to folders and files on disk.</summary>
        public DataFolder DataFolder { get; set; }

        /// <summary>Provider of date time functionality.</summary>
        public IDateTimeProvider DateTimeProvider { get; set; }

        /// <summary>Application life cycle control - triggers when application shuts down.</summary>
        private NodeLifetime nodeLifetime;

        /// <see cref="INodeStats"/>
        private INodeStats NodeStats { get; set; }

        private IAsyncLoop periodicLogLoop;

        private IAsyncLoop periodicBenchmarkLoop;

        private NodeRunningLock nodeRunningLock;

        /// <inheritdoc />
        public INodeLifetime NodeLifetime
        {
            get { return this.nodeLifetime; }
            private set { this.nodeLifetime = (NodeLifetime)value; }
        }

        /// <inheritdoc />
        public IFullNodeServiceProvider Services { get; set; }

        public T NodeService<T>(bool failWithDefault = false)
        {
            if (this.Services != null && this.Services.ServiceProvider != null)
            {
                var service = this.Services.ServiceProvider.GetService<T>();
                if (service != null)
                    return service;
            }

            if (failWithDefault)
                return default(T);

            throw new InvalidOperationException($"The {typeof(T).ToString()} service is not supported");
        }

        public T NodeFeature<T>(bool failWithError = false)
        {
            if (this.Services != null)
            {
                T feature = this.Services.Features.OfType<T>().FirstOrDefault();
                if (feature != null)
                    return feature;
            }

            if (!failWithError)
                return default(T);

            throw new InvalidOperationException($"The {typeof(T).ToString()} feature is not supported");
        }

        /// <inheritdoc />
        public Version Version
        {
            get
            {
                string versionString = typeof(FullNode).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;

                if (!string.IsNullOrEmpty(versionString))
                {
                    try
                    {
                        return new Version(versionString);
                    }
                    catch (ArgumentException e)
                    {
                        Console.WriteLine("{0}: {1}", e.GetType().Name, e.Message);
                    }
                    catch (OverflowException o)
                    {
                        Console.WriteLine("{0}: {1}", o.GetType().Name, o.Message);
                    }
                }

                return new Version(0, 0);
            }
        }

        /// <summary>Creates new instance of the <see cref="FullNode"/>.</summary>
        public FullNode()
        {
            this.State = FullNodeState.Created;
        }

        /// <inheritdoc />
        public IFullNode Initialize(IFullNodeServiceProvider serviceProvider)
        {
            this.State = FullNodeState.Initializing;

            Guard.NotNull(serviceProvider, nameof(serviceProvider));

            this.Services = serviceProvider;
            this.logger = this.Services.ServiceProvider.GetService<ILoggerFactory>().CreateLogger(this.GetType().FullName);
            this.DataFolder = this.Services.ServiceProvider.GetService<DataFolder>();

            this.DateTimeProvider = this.Services.ServiceProvider.GetService<IDateTimeProvider>();
            this.Network = this.Services.ServiceProvider.GetService<Network>();
            this.Settings = this.Services.ServiceProvider.GetService<NodeSettings>();
            this.ChainBehaviorState = this.Services.ServiceProvider.GetService<IChainState>();
            this.ChainIndexer = this.Services.ServiceProvider.GetService<ChainIndexer>();
            this.Signals = this.Services.ServiceProvider.GetService<Signals.ISignals>();
            this.InitialBlockDownloadState = this.Services.ServiceProvider.GetService<IInitialBlockDownloadState>();
            this.NodeStats = this.Services.ServiceProvider.GetService<INodeStats>();

            this.ConnectionManager = this.Services.ServiceProvider.GetService<IConnectionManager>();
            this.loggerFactory = this.Services.ServiceProvider.GetService<NodeSettings>().LoggerFactory;

            this.AsyncProvider = this.Services.ServiceProvider.GetService<IAsyncProvider>();

            this.logger.LogInformation(Properties.BlockcoreLogo.Logo);
            this.logger.LogInformation("Full node initialized on {0}.", this.Network.Name);

            this.State = FullNodeState.Initialized;
            this.StartTime = this.DateTimeProvider.GetUtcNow();
            return this;
        }

        /// <inheritdoc />
        public void Start()
        {
            this.State = FullNodeState.Starting;

            if (this.State is FullNodeState.Disposing or FullNodeState.Disposed)
                throw new ObjectDisposedException(nameof(FullNode));

            this.nodeRunningLock = new NodeRunningLock(this.DataFolder);

            if (!this.nodeRunningLock.TryLockNodeFolder())
                try {
                       this.logger.LogCritical("Node folder is being used by another instance of the application!");
                    }
              catch {
                    throw new Exception("Node folder is being used!");
                    }

            this.nodeLifetime = this.Services.ServiceProvider.GetRequiredService<INodeLifetime>() as NodeLifetime;
            this.fullNodeFeatureExecutor = this.Services.ServiceProvider.GetRequiredService<FullNodeFeatureExecutor>();

            if (this.nodeLifetime == null)
                throw new InvalidOperationException($"{nameof(INodeLifetime)} must be set.");

            if (this.fullNodeFeatureExecutor == null)
                throw new InvalidOperationException($"{nameof(FullNodeFeatureExecutor)} must be set.");

            this.logger.LogInformation("Starting node.");

            // Initialize all registered features.
            this.fullNodeFeatureExecutor.Initialize();

            // Initialize peer connection.
            var consensusManager = this.Services.ServiceProvider.GetRequiredService<IConsensusManager>();
            this.ConnectionManager.Initialize(consensusManager);

            // Fire INodeLifetime.Started.
            this.nodeLifetime.NotifyStarted();

            this.StartPeriodicLog();

            this.State = FullNodeState.Started;
        }

        /// <summary>
        /// Starts a loop to periodically log statistics about node's status very couple of seconds.
        /// <para>
        /// These logs are also displayed on the console.
        /// </para>
        /// </summary>
        private void StartPeriodicLog()
        {
            this.periodicLogLoop = this.AsyncProvider.CreateAndRunAsyncLoop("PeriodicLog", (cancellation) =>
            {
                string stats = this.NodeStats.GetStats();

                this.logger.LogInformation(stats);
                this.LastLogOutput = stats;

                return Task.CompletedTask;
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpans.FiveSeconds,
            startAfter: TimeSpans.FiveSeconds);

            this.periodicBenchmarkLoop = this.AsyncProvider.CreateAndRunAsyncLoop("PeriodicBenchmarkLog", (cancellation) =>
            {
                if (this.InitialBlockDownloadState.IsInitialBlockDownload())
                {
                    string benchmark = this.NodeStats.GetBenchmark();
                    this.logger.LogInformation(benchmark);
                }

                return Task.CompletedTask;
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpan.FromSeconds(17),
            startAfter: TimeSpan.FromSeconds(17));
        }

        public string LastLogOutput { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.State is FullNodeState.Disposing or FullNodeState.Disposed)
                return;

            this.State = FullNodeState.Disposing;

            this.logger.LogInformation("Closing node pending.");

            // Fire INodeLifetime.Stopping.
            this.nodeLifetime.StopApplication();

            this.logger.LogInformation("Disposing connection manager.");
            this.ConnectionManager.Dispose();

            this.logger.LogInformation("Disposing periodic logging loops.");
            this.periodicLogLoop?.Dispose();
            this.periodicBenchmarkLoop?.Dispose();

            // Fire the NodeFeatureExecutor.Stop.
            this.logger.LogInformation("Disposing the full node feature executor.");
            this.fullNodeFeatureExecutor.Dispose();

            this.logger.LogInformation("Disposing settings.");
            this.Settings.Dispose();

            // Fire INodeLifetime.Stopped.
            this.logger.LogInformation("Notify application has stopped.");
            this.nodeLifetime.NotifyStopped();

            this.nodeRunningLock.UnlockNodeFolder();

            this.State = FullNodeState.Disposed;
        }
    }
}