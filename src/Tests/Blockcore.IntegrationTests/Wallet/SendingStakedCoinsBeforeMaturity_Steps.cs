﻿using System.Collections.Generic;
using System.Linq;
using Blockcore.Features.Wallet.Api.Controllers;
using Blockcore.Features.Wallet.Api.Models;
using Blockcore.Features.Wallet.Types;
using Blockcore.IntegrationTests.Common;
using Blockcore.IntegrationTests.Common.EnvironmentMockUpHelpers;
using Blockcore.IntegrationTests.Common.Extensions;
using Blockcore.Networks.Stratis;
using Blockcore.Tests.Common.TestFramework;
using Blockcore.Utilities.JsonErrors;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Xunit.Abstractions;

namespace Blockcore.IntegrationTests.Wallet
{
    public partial class SendingStakedCoinsBeforeMaturity : BddSpecification
    {
        private ProofOfStakeSteps proofOfStakeSteps;
        private const decimal OneMillion = 1_000_000;
        private CoreNode receiverNode;
        private const string WalletName = "mywallet";
        private const string WalletPassword = "123456";
        private const string WalletPassphrase = "passphrase";
        private const string WalletAccountName = "account 0";

        public SendingStakedCoinsBeforeMaturity(ITestOutputHelper outputHelper)
            : base(outputHelper)
        { }

        protected override void BeforeTest()
        {
            this.proofOfStakeSteps = new ProofOfStakeSteps(this.CurrentTest.DisplayName);
        }

        protected override void AfterTest()
        {
            this.proofOfStakeSteps.nodeBuilder.Dispose();
        }

        private void two_pos_nodes_with_one_node_having_a_wallet_with_premined_coins()
        {
            this.proofOfStakeSteps.PremineNodeWithWallet("ssc-pmnode");
            this.proofOfStakeSteps.MineGenesisAndPremineBlocks();

            this.receiverNode = this.proofOfStakeSteps.nodeBuilder.CreateStratisPosNode(new StratisRegTest(), "ssc-receiver").WithWallet().Start();

            TestHelper.ConnectAndSync(this.proofOfStakeSteps.PremineNodeWithCoins, this.receiverNode);
        }

        private void a_wallet_sends_coins_before_maturity()
        {
            the_wallet_history_does_not_include_the_transaction();

            IActionResult buildTransactionResult = BuildTransaction();

            buildTransactionResult.Should().BeOfType<ErrorResult>();

            var error = buildTransactionResult as ErrorResult;
            error.StatusCode.Should().Be(400);

            var errorResponse = error.Value as ErrorResponse;
            errorResponse?.Errors.Count.Should().Be(1);
            errorResponse?.Errors[0].Message.Should().Be("No spendable transactions found.");

            IActionResult sendTransactionResult = SendTransaction(buildTransactionResult);
            sendTransactionResult.Should().BeNull();
        }

        private IActionResult SendTransaction(IActionResult transactionResult)
        {
            var walletTransactionModel = (WalletBuildTransactionModel)(transactionResult as JsonResult)?.Value;
            if (walletTransactionModel == null)
                return null;

            return this.proofOfStakeSteps.PremineNodeWithCoins.FullNode.NodeController<WalletController>().SendTransaction(new SendTransactionRequest(walletTransactionModel.Hex));
        }

        private IActionResult BuildTransaction()
        {
            IActionResult transactionResult = this.proofOfStakeSteps.PremineNodeWithCoins.FullNode.NodeController<WalletController>()
                .BuildTransaction(new BuildTransactionRequest
                {
                    AccountName = this.proofOfStakeSteps.PremineWalletAccount,
                    AllowUnconfirmed = true,
                    Recipients = new List<RecipientModel> { new RecipientModel { DestinationAddress = GetReceiverUnusedAddressFromWallet(), Amount = Money.Coins(OneMillion + 40).ToString() } },
                    FeeType = FeeType.Medium.ToString("D"),
                    Password = this.proofOfStakeSteps.PremineWalletPassword,
                    WalletName = this.proofOfStakeSteps.PremineWallet,
                    FeeAmount = Money.Satoshis(20000).ToString()
                });

            return transactionResult;
        }

        private void the_wallet_history_does_not_include_the_transaction()
        {
            WalletHistoryModel walletHistory = GetWalletHistory(this.proofOfStakeSteps.PremineNodeWithCoins, this.proofOfStakeSteps.PremineWallet);
            AccountHistoryModel accountHistory = walletHistory.AccountsHistoryModel.FirstOrDefault();

            accountHistory?.TransactionsHistory?.Where(txn => txn.Type == TransactionItemType.Send).Count().Should().Be(0);
        }

        private void the_transaction_was_not_received()
        {
            this.receiverNode.FullNode.WalletManager().GetSpendableTransactionsInWallet(WalletName).Sum(utxo => utxo.Transaction.Amount).Should().Be(0);
        }

        private WalletHistoryModel GetWalletHistory(CoreNode node, string walletName)
        {
            var walletHistory = node.FullNode.NodeController<WalletController>().GetHistory(new WalletHistoryRequest { WalletName = walletName }) as JsonResult;
            return walletHistory?.Value as WalletHistoryModel;
        }

        private string GetReceiverUnusedAddressFromWallet()
        {
            return this.receiverNode.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference(WalletName, WalletAccountName)).Address;
        }
    }
}