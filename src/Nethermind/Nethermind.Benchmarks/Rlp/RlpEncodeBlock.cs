﻿/*
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

using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Mathematics.StatisticalTesting;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Rlp
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class RlpEncodeBlock
    {
        private static BlockDecoder _blockDecoder = new BlockDecoder();
        
        private static Block _block;

        private Block[] _scenarios;

        public RlpEncodeBlock()
        {
            var transactions = new Transaction[100];
            for (int i = 0; i < 100; i++)
            {
                transactions[i] = Build.A.Transaction.WithData(new byte[] {(byte) i}).WithNonce((UInt256) i).WithValue((UInt256) i).Signed(new EthereumEcdsa(MainNetSpecProvider.Instance, NullLogManager.Instance), TestItem.PrivateKeyA, 1L).TestObject;
            }

            _scenarios = new[]
            {
                Build.A.Block.WithNumber(1).TestObject,
                Build.A.Block.WithNumber(1).WithTransactions(transactions).WithOmmers(Build.A.BlockHeader.TestObject).WithMixHash(Keccak.EmptyTreeHash).TestObject
            };
        }
        
        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _block = _scenarios[ScenarioIndex];
            Check(Current(), Improved());
            Check(Current(), Improved2());
        }

        private RecyclableMemoryStreamManager _recycler = new RecyclableMemoryStreamManager();
        
        [Benchmark]
        public byte[] Improved()
        {
            using (MemoryStream stream = _recycler.GetStream())
            {
                Nethermind.Core.Encoding.Rlp.Encode(stream, _block);
                return stream.ToArray();
            }
        }
        
        [Benchmark]
        public byte[] Improved2()
        {
            return _blockDecoder.Encode(_block).Bytes;
        }

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Nethermind.Core.Encoding.Rlp.Encode(_block).Bytes;
        }
    }
}