/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositConfirmationService : IDepositConfirmationService
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IDepositService _depositService;
        private readonly ILogger _logger;
        private readonly uint _requiredBlockConfirmations;

        public DepositConfirmationService(IBlockchainBridge blockchainBridge, IConsumerNotifier consumerNotifier,
            IDepositDetailsRepository depositRepository, IDepositService depositService, ILogManager logManager,
            uint requiredBlockConfirmations)
        {
            _blockchainBridge = blockchainBridge;
            _consumerNotifier = consumerNotifier;
            _depositRepository = depositRepository;
            _depositService = depositService;
            _logger = logManager.GetClassLogger();
            _requiredBlockConfirmations = requiredBlockConfirmations;
        }
        
        public async Task TryConfirmAsync(DepositDetails deposit)
        {
            if (deposit.Confirmed || deposit.Rejected)
            {
                return;
            }

            var head = _blockchainBridge.Head;
            var transactionHash = deposit.TransactionHash;
            var (receipt, transaction) = _blockchainBridge.GetTransaction(deposit.TransactionHash);                        
            if (transaction is null)
            {
                if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{transactionHash}' for deposit: '{deposit.Id}' to be confirmed.");
                return;
            }

            var (confirmations, rejected) = await VerifyDepositConfirmationsAsync(deposit, receipt, head.Hash);
            if (rejected)
            {
                deposit.Reject();
                await _depositRepository.UpdateAsync(deposit);
                await _consumerNotifier.SendDepositRejectedAsync(deposit.Id);
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has {confirmations} confirmations (required at least {_requiredBlockConfirmations}) for transaction hash: '{transactionHash}' to be confirmed.");
            var confirmed = confirmations >= _requiredBlockConfirmations;
            if (confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Deposit.Id}' has been confirmed.");
            }
            
            if (confirmations != deposit.Confirmations || confirmed)
            {
                deposit.SetConfirmations(confirmations);
                await _depositRepository.UpdateAsync(deposit);
            }

            await _consumerNotifier.SendDepositConfirmationsStatusAsync(deposit.Id, deposit.DataAsset.Name,
                confirmations, _requiredBlockConfirmations, deposit.ConfirmationTimestamp, confirmed);
        }
        
        private async Task<(uint confirmations, bool rejected)> VerifyDepositConfirmationsAsync(DepositDetails deposit,
            TxReceipt receipt, Keccak headHash)
        {
            var confirmations = 0u;
            var block = _blockchainBridge.FindBlock(headHash);
            do
            {
                if (block is null)
                {
                    if (_logger.IsWarn) _logger.Warn("Block was not found.");
                    return (0, false);
                }

                var confirmationTimestamp = _depositService.VerifyDeposit(deposit.Consumer, deposit.Id, block.Header);
                if (confirmationTimestamp > 0)
                {
                    confirmations++;
                    if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has been confirmed in block number: {block.Number}, hash: '{block.Hash}', transaction hash: '{deposit.TransactionHash}', timestamp: {confirmationTimestamp}.");
                    if (deposit.ConfirmationTimestamp == 0)
                    {
                        deposit.SetConfirmationTimestamp(confirmationTimestamp);
                        await _depositRepository.UpdateAsync(deposit);
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Id}' has not returned confirmation timestamp from the contract call yet.'");
                    return (0, false);
                }
                
                if (confirmations == _requiredBlockConfirmations)
                {
                    break;
                }

                if (receipt.BlockHash == block.Hash || block.Number <= receipt.BlockNumber)
                {
                    break;
                }

                block = _blockchainBridge.FindBlock(block.ParentHash);
            } while (confirmations < _requiredBlockConfirmations);
            
            var blocksDifference = _blockchainBridge.Head.Number - receipt.BlockNumber;
            if (blocksDifference >= _requiredBlockConfirmations && confirmations < _requiredBlockConfirmations)
            {
                if (_logger.IsError) _logger.Error($"Deposit: '{deposit.Id}' has been rejected - missing confirmation in block number: {block.Number}, hash: {block.Hash}' (transaction hash: '{deposit.TransactionHash}').");
                return (confirmations, true);
            }

            return (confirmations, false);
        }
    }
}