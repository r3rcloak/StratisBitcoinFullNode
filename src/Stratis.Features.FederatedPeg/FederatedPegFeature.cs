﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Configuration.Logging;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Features.Api;
using Stratis.Bitcoin.Features.Consensus;
using Stratis.Bitcoin.Features.Consensus.CoinViews;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Features.Notifications;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Features.PoA.Voting;
using Stratis.Bitcoin.Features.SmartContracts;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.P2P.Peer;
using Stratis.Bitcoin.P2P.Protocol.Payloads;
using Stratis.Bitcoin.Utilities;
using Stratis.Features.FederatedPeg.Collateral;
using Stratis.Features.FederatedPeg.Controllers;
using Stratis.Features.FederatedPeg.CounterChain;
using Stratis.Features.FederatedPeg.Interfaces;
using Stratis.Features.FederatedPeg.Models;
using Stratis.Features.FederatedPeg.Notifications;
using Stratis.Features.FederatedPeg.Payloads;
using Stratis.Features.FederatedPeg.SourceChain;
using Stratis.Features.FederatedPeg.TargetChain;
using Stratis.Features.FederatedPeg.Wallet;
using TracerAttributes;

//todo: this is pre-refactoring code
//todo: ensure no duplicate or fake withdrawal or deposit transactions are possible (current work underway)

namespace Stratis.Features.FederatedPeg
{
    internal class FederatedPegFeature : FullNodeFeature
    {
        /// <summary>
        /// Given that we can have up to 10 UTXOs going at once.
        /// </summary>
        private const int TransfersToDisplay = 10;

        /// <summary>
        /// The maximum number of pending transactions to display in the console logging.
        /// </summary>
        private const int PendingToDisplay = 25;

        public const string FederationGatewayFeatureNamespace = "federationgateway";

        private readonly IConnectionManager connectionManager;

        private readonly IFederatedPegSettings federatedPegSettings;

        private readonly IFullNode fullNode;

        private readonly ILoggerFactory loggerFactory;

        private readonly IFederationWalletManager federationWalletManager;

        private readonly IFederationWalletSyncManager walletSyncManager;

        private readonly ChainIndexer chainIndexer;

        private readonly Network network;

        private readonly ICrossChainTransferStore crossChainTransferStore;

        private readonly IPartialTransactionRequester partialTransactionRequester;

        private readonly ISignedMultisigTransactionBroadcaster signedBroadcaster;

        private readonly IMaturedBlocksSyncManager maturedBlocksSyncManager;

        private readonly IWithdrawalHistoryProvider withdrawalHistoryProvider;

        private readonly ILogger logger;

        public FederatedPegFeature(
            ILoggerFactory loggerFactory,
            IConnectionManager connectionManager,
            IFederatedPegSettings federatedPegSettings,
            IFullNode fullNode,
            IFederationWalletManager federationWalletManager,
            IFederationWalletSyncManager walletSyncManager,
            Network network,
            ChainIndexer chainIndexer,
            INodeStats nodeStats,
            ICrossChainTransferStore crossChainTransferStore,
            IPartialTransactionRequester partialTransactionRequester,
            ISignedMultisigTransactionBroadcaster signedBroadcaster,
            IMaturedBlocksSyncManager maturedBlocksSyncManager,
            IWithdrawalHistoryProvider withdrawalHistoryProvider,
            ICollateralChecker collateralChecker = null)
        {
            this.loggerFactory = loggerFactory;
            this.connectionManager = connectionManager;
            this.federatedPegSettings = federatedPegSettings;
            this.fullNode = fullNode;
            this.chainIndexer = chainIndexer;
            this.federationWalletManager = federationWalletManager;
            this.walletSyncManager = walletSyncManager;
            this.network = network;
            this.crossChainTransferStore = crossChainTransferStore;
            this.partialTransactionRequester = partialTransactionRequester;
            this.maturedBlocksSyncManager = maturedBlocksSyncManager;
            this.withdrawalHistoryProvider = withdrawalHistoryProvider;
            this.signedBroadcaster = signedBroadcaster;

            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);

            // add our payload
            var payloadProvider = (PayloadProvider)this.fullNode.Services.ServiceProvider.GetService(typeof(PayloadProvider));
            payloadProvider.AddPayload(typeof(RequestPartialTransactionPayload));

            nodeStats.RegisterStats(this.AddComponentStats, StatsType.Component);
            nodeStats.RegisterStats(this.AddInlineStats, StatsType.Inline, 800);
        }

        public override async Task InitializeAsync()
        {
            // Set up our database of deposit and withdrawal transactions. Needs to happen before everything else.
            this.crossChainTransferStore.Initialize();

            // Load the federation wallet that will be used to generate transactions.
            this.federationWalletManager.Start();

            // Query the other chain every N seconds for deposits. Triggers signing process if deposits are found.
            this.maturedBlocksSyncManager.Start();

            // Syncs the wallet correctly when restarting the node. i.e. deals with reorgs.
            this.walletSyncManager.Initialize();

            // Synchronises the wallet and the transfer store.
            this.crossChainTransferStore.Start();

            // Query our database for partially-signed transactions and send them around to be signed every N seconds.
            this.partialTransactionRequester.Start();

            // Query our database for fully-signed transactions and broadcast them every N seconds.
            this.signedBroadcaster.Start();

            // Connect the node to the other federation members.
            foreach (IPEndPoint federationMemberIp in this.federatedPegSettings.FederationNodeIpEndPoints)
                this.connectionManager.AddNodeAddress(federationMemberIp);

            // Respond to requests to sign transactions from other nodes.
            NetworkPeerConnectionParameters networkPeerConnectionParameters = this.connectionManager.Parameters;
            networkPeerConnectionParameters.TemplateBehaviors.Add(new PartialTransactionsBehavior(this.loggerFactory, this.federationWalletManager,
                this.network, this.federatedPegSettings, this.crossChainTransferStore));
        }

        public override void Dispose()
        {
            // Sync manager has to be disposed BEFORE cross chain transfer store.
            this.maturedBlocksSyncManager.Dispose();

            this.crossChainTransferStore.Dispose();
        }

        private void AddInlineStats(StringBuilder benchLogs)
        {
            if (this.federationWalletManager == null)
                return;

            int height = this.federationWalletManager.LastBlockHeight();
            ChainedHeader block = this.chainIndexer.GetHeader(height);
            uint256 hashBlock = block == null ? 0 : block.HashBlock;

            FederationWallet federationWallet = this.federationWalletManager.GetWallet();
            benchLogs.AppendLine("Fed. Wallet.Height: ".PadRight(LoggingConfiguration.ColumnLength + 1) +
                                 (federationWallet != null ? height.ToString().PadRight(8) : "No Wallet".PadRight(8)) +
                                 (federationWallet != null ? (" Fed. Wallet.Hash: ".PadRight(LoggingConfiguration.ColumnLength - 1) + hashBlock) : string.Empty));
        }

        private void AddComponentStats(StringBuilder benchLog)
        {
            try
            {
                string stats = this.CollectStats();
                benchLog.Append(stats);
            }
            catch (Exception e)
            {
                this.logger.LogError(e.ToString());
            }
        }

        [NoTrace]
        private string CollectStats()
        {
            StringBuilder benchLog = new StringBuilder();
            benchLog.AppendLine();
            benchLog.AppendLine("====== Federation Wallet ======");

            (Money ConfirmedAmount, Money UnConfirmedAmount) balances = this.federationWalletManager.GetSpendableAmount();
            bool isFederationActive = this.federationWalletManager.IsFederationWalletActive();
            benchLog.AppendLine("Federation Wallet: ".PadRight(LoggingConfiguration.ColumnLength)
                                + " Confirmed balance: " + balances.ConfirmedAmount.ToString().PadRight(LoggingConfiguration.ColumnLength)
                                + " Reserved for withdrawals: " + balances.UnConfirmedAmount.ToString().PadRight(LoggingConfiguration.ColumnLength)
                                + " Federation Status: " + (isFederationActive ? "Active" : "Inactive"));
            benchLog.AppendLine();

            if (!isFederationActive)
            {
                var apiSettings = (ApiSettings)this.fullNode.Services.ServiceProvider.GetService(typeof(ApiSettings));

                benchLog.AppendLine("".PadRight(59, '=') + " W A R N I N G " + "".PadRight(59, '='));
                benchLog.AppendLine();
                benchLog.AppendLine("This federation node is not enabled. You will not be able to store or participate in signing of transactions until you enable it.");
                benchLog.AppendLine("If not done previously, please enable your federation node using " + $"{apiSettings.ApiUri}/api/FederationWallet/{FederationWalletRouteEndPoint.EnableFederation}.");
                benchLog.AppendLine();
                benchLog.AppendLine("".PadRight(133, '='));
                benchLog.AppendLine();
            }

            try
            {
                List<WithdrawalModel> pendingWithdrawals = this.withdrawalHistoryProvider.GetPending();

                if (pendingWithdrawals.Count > 0)
                {
                    benchLog.AppendLine("--- Pending Withdrawals ---");
                    foreach (WithdrawalModel withdrawal in pendingWithdrawals.Take(PendingToDisplay))
                        benchLog.AppendLine(withdrawal.ToString());

                    if (pendingWithdrawals.Count > PendingToDisplay)
                        benchLog.AppendLine($"And {pendingWithdrawals.Count - PendingToDisplay} more...");

                    benchLog.AppendLine();
                }
            }
            catch (Exception exception)
            {
                benchLog.AppendLine("--- Pending Withdrawals ---");
                benchLog.AppendLine("Failed to retrieve data");
                this.logger.LogDebug("Exception occurred while getting pending withdrawals: '{0}'.", exception.ToString());
            }

            List<WithdrawalModel> completedWithdrawals = this.withdrawalHistoryProvider.GetHistory(TransfersToDisplay);

            if (completedWithdrawals.Count > 0)
            {
                benchLog.AppendLine("--- Recently Completed Withdrawals ---");
                foreach (WithdrawalModel withdrawal in completedWithdrawals)
                    benchLog.AppendLine(withdrawal.ToString());
                benchLog.AppendLine();
            }

            benchLog.AppendLine("====== NodeStore ======");
            this.AddBenchmarkLine(benchLog, new (string, int)[] {
                ("Height:", LoggingConfiguration.ColumnLength),
                (this.crossChainTransferStore.TipHashAndHeight.Height.ToString(), LoggingConfiguration.ColumnLength),
                ("Hash:",LoggingConfiguration.ColumnLength),
                (this.crossChainTransferStore.TipHashAndHeight.HashBlock.ToString(), 0),
                ("NextDepositHeight:", LoggingConfiguration.ColumnLength),
                (this.crossChainTransferStore.NextMatureDepositHeight.ToString(), LoggingConfiguration.ColumnLength),
                ("HasSuspended:",LoggingConfiguration.ColumnLength),
                (this.crossChainTransferStore.HasSuspended().ToString(), 0)
            },
            4);

            this.AddBenchmarkLine(benchLog,
                this.crossChainTransferStore.GetCrossChainTransferStatusCounter().SelectMany(item => new (string, int)[]{
                    (item.Key.ToString()+":", LoggingConfiguration.ColumnLength),
                    (item.Value.ToString(), LoggingConfiguration.ColumnLength)
                    }).ToArray(),
                4);

            benchLog.AppendLine();
            return benchLog.ToString();
        }

        private void AddBenchmarkLine(StringBuilder benchLog, (string Value, int ValuePadding)[] items, int maxItemsPerLine = int.MaxValue)
        {
            if (items != null)
            {
                int itemsAdded = 0;
                foreach (var item in items)
                {
                    if (itemsAdded++ >= maxItemsPerLine)
                    {
                        benchLog.AppendLine();
                        itemsAdded = 1;
                    }
                    benchLog.Append(item.Value.PadRight(item.ValuePadding));
                }
                benchLog.AppendLine();
            }
        }
    }

    /// <summary>
    /// A class providing extension methods for <see cref="IFullNodeBuilder"/>.
    /// </summary>
    public static class FullNodeBuilderSidechainRuntimeFeatureExtension
    {
        public static IFullNodeBuilder AddFederatedPeg(this IFullNodeBuilder fullNodeBuilder)
        {
            LoggingConfiguration.RegisterFeatureNamespace<FederatedPegFeature>(
                FederatedPegFeature.FederationGatewayFeatureNamespace);

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features.AddFeature<FederatedPegFeature>()
                    .DependOn<BlockNotificationFeature>()
                    .DependOn<CounterChainFeature>()
                    .FeatureServices(services =>
                    {
                        services.AddSingleton<IMaturedBlocksProvider, MaturedBlocksProvider>();
                        services.AddSingleton<IFederatedPegSettings, FederatedPegSettings>();
                        services.AddSingleton<IOpReturnDataReader, OpReturnDataReader>();
                        services.AddSingleton<IDepositExtractor, DepositExtractor>();
                        services.AddSingleton<IWithdrawalExtractor, WithdrawalExtractor>();
                        services.AddSingleton<FederationGatewayController>();
                        services.AddSingleton<IFederationWalletSyncManager, FederationWalletSyncManager>();
                        services.AddSingleton<IFederationWalletTransactionHandler, FederationWalletTransactionHandler>();
                        services.AddSingleton<IFederationWalletManager, FederationWalletManager>();
                        services.AddSingleton<IWithdrawalTransactionBuilder, WithdrawalTransactionBuilder>();
                        services.AddSingleton<FederationWalletController>();
                        services.AddSingleton<ICrossChainTransferStore, CrossChainTransferStore>();
                        services.AddSingleton<ISignedMultisigTransactionBroadcaster, SignedMultisigTransactionBroadcaster>();
                        services.AddSingleton<IPartialTransactionRequester, PartialTransactionRequester>();
                        services.AddSingleton<IFederationGatewayClient, FederationGatewayClient>();
                        services.AddSingleton<IMaturedBlocksSyncManager, MaturedBlocksSyncManager>();
                        services.AddSingleton<IWithdrawalHistoryProvider, WithdrawalHistoryProvider>();
                        services.AddSingleton<FederatedPegSettings>();

                        // Set up events.
                        services.AddSingleton<TransactionObserver>();
                        services.AddSingleton<BlockObserver>();
                    });
            });

            return fullNodeBuilder;
        }

        public static IFullNodeBuilder UseFederatedPegPoAMining(this IFullNodeBuilder fullNodeBuilder)
        {
            fullNodeBuilder.ConfigureFeature(features =>
            {
                features.AddFeature<PoAFeature>().DependOn<FederatedPegFeature>().FeatureServices(services =>
                    {
                        services.AddSingleton<PoABlockHeaderValidator>();
                        services.AddSingleton<IPoAMiner, CollateralPoAMiner>();
                        services.AddSingleton<ISlotsManager, SlotsManager>();
                        services.AddSingleton<BlockDefinition, FederatedPegBlockDefinition>();
                        services.AddSingleton<ICoinbaseSplitter, PremineCoinbaseSplitter>();
                        services.AddSingleton<IBlockBufferGenerator, BlockBufferGenerator>();
                    });
            });

            // TODO: Consensus and Mining should be separated. Sidechain nodes don't need any of the Federation code but do need Consensus.
            // In the dependency tree as it is currently however, consensus is dependent on PoAFeature (needs SlotManager) which is in turn dependent on
            // FederatedPegFeature. https://github.com/stratisproject/FederatedSidechains/issues/273

            LoggingConfiguration.RegisterFeatureNamespace<ConsensusFeature>("consensus");
            fullNodeBuilder.ConfigureFeature(features =>
            {
                features.AddFeature<ConsensusFeature>().FeatureServices(services =>
                {
                    services.AddSingleton<DBreezeCoinView>();
                    services.AddSingleton<ICoinView, CachedCoinView>();
                    services.AddSingleton<ConsensusController>();
                    services.AddSingleton<IChainState, ChainState>();
                    services.AddSingleton<ConsensusQuery>()
                        .AddSingleton<INetworkDifficulty, ConsensusQuery>(provider => provider.GetService<ConsensusQuery>())
                        .AddSingleton<IGetUnspentTransaction, ConsensusQuery>(provider => provider.GetService<ConsensusQuery>());

                    services.AddSingleton<VotingManager>();
                    services.AddSingleton<IPollResultExecutor, PollResultExecutor>();
                    services.AddSingleton<IWhitelistedHashesRepository, WhitelistedHashesRepository>();
                    services.AddSingleton<IdleFederationMembersKicker>();
                    services.AddSingleton<PoAMinerSettings>();
                    services.AddSingleton<MinerSettings>();

                    // Consensus Rules
                    services.AddSingleton<PoAConsensusRuleEngine>();
                    services.AddSingleton<DefaultVotingController>();
                });
            });

            return fullNodeBuilder;
        }
    }
}