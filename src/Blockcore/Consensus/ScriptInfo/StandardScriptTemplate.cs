﻿using System;
using System.Collections.Generic;
using System.Linq;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Networks;
using NBitcoin;
using NBitcoin.Crypto;

namespace Blockcore.Consensus.ScriptInfo
{
    //TODO : Is*Conform can be used to parses the script

    public enum TxOutType
    {
        TX_NONSTANDARD,

        // 'standard' transaction types:
        TX_PUBKEY,

        TX_PUBKEYHASH,
        TX_SCRIPTHASH,
        TX_MULTISIG,
        TX_NULL_DATA,
        TX_SEGWIT,
        TX_COLDSTAKE
    };

    public class TxNullDataTemplate : ScriptTemplate
    {
        public TxNullDataTemplate(int maxScriptSize, int minSatoshiFee = 0)
        {
            this.MinRequiredSatoshiFee = minSatoshiFee;
            this.MaxScriptSizeLimit = maxScriptSize;
        }

        private static readonly TxNullDataTemplate _Instance = new TxNullDataTemplate(MAX_OP_RETURN_RELAY);

        public static TxNullDataTemplate Instance
        {
            get
            {
                return _Instance;
            }
        }

        public int MaxScriptSizeLimit
        {
            get;
            private set;
        }

        public int MinRequiredSatoshiFee
        {
            get;
            private set;
        }

        protected override bool FastCheckScriptPubKey(Script scriptPubKey, out bool needMoreCheck)
        {
            byte[] bytes = scriptPubKey.ToBytes(true);
            if (bytes.Length == 0 ||
                bytes[0] != (byte)OpcodeType.OP_RETURN ||
                bytes.Length > this.MaxScriptSizeLimit)
            {
                needMoreCheck = false;
                return false;
            }
            needMoreCheck = true;
            return true;
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            return scriptPubKeyOps.Skip(1).All(o => o.PushData != null && !o.IsInvalid);
        }

        public byte[][] ExtractScriptPubKeyParameters(Script scriptPubKey)
        {
            bool needMoreCheck;
            if (!FastCheckScriptPubKey(scriptPubKey, out needMoreCheck))
                return null;

            Op[] ops = scriptPubKey.ToOps().ToArray();
            if (!CheckScriptPubKeyCore(scriptPubKey, ops))
                return null;

            return ops.Skip(1).Select(o => o.PushData).ToArray();
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            return false;
        }

        public const int MAX_OP_RETURN_RELAY = 83; //! bytes (+1 for OP_RETURN, +2 for the pushdata opcodes)

        public Script GenerateScriptPubKey(params byte[][] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            var ops = new Op[data.Length + 1];
            ops[0] = OpcodeType.OP_RETURN;
            for (int i = 0; i < data.Length; i++)
            {
                ops[1 + i] = Op.GetPushOp(data[i]);
            }
            var script = new Script(ops);
            if (script.ToBytes(true).Length > this.MaxScriptSizeLimit)
                throw new ArgumentOutOfRangeException("data", "Data in OP_RETURN should have a maximum size of " + this.MaxScriptSizeLimit + " bytes");
            return script;
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_NULL_DATA;
            }
        }
    }

    public class PayToMultiSigTemplateParameters
    {
        public int SignatureCount
        {
            get;
            set;
        }

        public PubKey[] PubKeys
        {
            get;
            set;
        }

        public byte[][] InvalidPubKeys
        {
            get;
            set;
        }
    }

    public class PayToMultiSigTemplate : ScriptTemplate
    {
        private static readonly PayToMultiSigTemplate _Instance = new PayToMultiSigTemplate();

        public static PayToMultiSigTemplate Instance
        {
            get
            {
                return _Instance;
            }
        }

        public Script GenerateScriptPubKey(int sigCount, params PubKey[] keys)
        {
            var ops = new List<Op>();
            Op push = Op.GetPushOp(sigCount);
            if (!push.IsSmallUInt)
                throw new ArgumentOutOfRangeException("sigCount should be less or equal to 16");
            ops.Add(push);
            Op keyCount = Op.GetPushOp(keys.Length);
            if (!keyCount.IsSmallUInt)
                throw new ArgumentOutOfRangeException("key count should be less or equal to 16");
            foreach (PubKey key in keys)
            {
                ops.Add(Op.GetPushOp(key.ToBytes()));
            }
            ops.Add(keyCount);
            ops.Add(OpcodeType.OP_CHECKMULTISIG);
            return new Script(ops);
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            Op[] ops = scriptPubKeyOps;
            if (ops.Length < 3)
                return false;

            int? sigCount = ops[0].GetInt();
            int? keyCount = ops[ops.Length - 2].GetInt();

            if (sigCount == null || keyCount == null)
                return false;
            if (keyCount.Value < 0 || keyCount.Value > 20)
                return false;
            if (sigCount.Value < 0 || sigCount.Value > keyCount.Value)
                return false;
            if (1 + keyCount + 1 + 1 != ops.Length)
                return false;
            for (int i = 1; i < keyCount + 1; i++)
            {
                if (ops[i].PushData == null)
                    return false;
            }
            return ops[ops.Length - 1].Code == OpcodeType.OP_CHECKMULTISIG;
        }

        public PayToMultiSigTemplateParameters ExtractScriptPubKeyParameters(Script scriptPubKey)
        {
            bool needMoreCheck;
            if (!FastCheckScriptPubKey(scriptPubKey, out needMoreCheck))
                return null;
            Op[] ops = scriptPubKey.ToOps().ToArray();
            if (!CheckScriptPubKeyCore(scriptPubKey, ops))
                return null;

            //already checked in CheckScriptPubKeyCore
            int sigCount = ops[0].GetInt().Value;
            int keyCount = ops[ops.Length - 2].GetInt().Value;
            var keys = new List<PubKey>();
            var invalidKeys = new List<byte[]>();
            for (int i = 1; i < keyCount + 1; i++)
            {
                if (!PubKey.Check(ops[i].PushData, false))
                    invalidKeys.Add(ops[i].PushData);
                else
                {
                    try
                    {
                        keys.Add(new PubKey(ops[i].PushData));
                    }
                    catch (FormatException)
                    {
                        invalidKeys.Add(ops[i].PushData);
                    }
                }
            }

            return new PayToMultiSigTemplateParameters()
            {
                SignatureCount = sigCount,
                PubKeys = keys.ToArray(),
                InvalidPubKeys = invalidKeys.ToArray()
            };
        }

        protected override bool FastCheckScriptSig(Script scriptSig, Script scriptPubKey, out bool needMoreCheck)
        {
            byte[] bytes = scriptSig.ToBytes(true);
            if (bytes.Length == 0 ||
                   bytes[0] != (byte)OpcodeType.OP_0)
            {
                needMoreCheck = false;
                return false;
            }
            needMoreCheck = true;
            return true;
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            if (!scriptSig.IsPushOnly)
                return false;
            if (scriptSigOps[0].Code != OpcodeType.OP_0)
                return false;
            if (scriptSigOps.Length == 1)
                return false;
            if (!scriptSigOps.Skip(1).All(s => TransactionSignature.ValidLength(s.PushData.Length) || s.Code == OpcodeType.OP_0))
                return false;
            if (scriptPubKeyOps != null)
            {
                if (!CheckScriptPubKeyCore(scriptPubKey, scriptPubKeyOps))
                    return false;
                int? sigCountExpected = scriptPubKeyOps[0].GetInt();
                if (sigCountExpected == null)
                    return false;
                return sigCountExpected == scriptSigOps.Length + 1;
            }
            return true;
        }

        public TransactionSignature[] ExtractScriptSigParameters(Network network, Script scriptSig)
        {
            bool needMoreCheck;
            if (!FastCheckScriptSig(scriptSig, null, out needMoreCheck))
                return new TransactionSignature[0];
            Op[] ops = scriptSig.ToOps().ToArray();
            if (!CheckScriptSigCore(network, scriptSig, ops, null, null))
                return new TransactionSignature[0];
            try
            {
                return ops.Skip(1).Select(i => i.Code == OpcodeType.OP_0 ? null : new TransactionSignature(i.PushData)).ToArray();
            }
            catch (FormatException)
            {
                return new TransactionSignature[0];
            }
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_MULTISIG;
            }
        }

        public Script GenerateScriptSig(TransactionSignature[] signatures)
        {
            return GenerateScriptSig((IEnumerable<TransactionSignature>)signatures);
        }

        public Script GenerateScriptSig(IEnumerable<TransactionSignature> signatures)
        {
            var ops = new List<Op>();
            ops.Add(OpcodeType.OP_0);
            foreach (TransactionSignature sig in signatures)
            {
                if (sig == null)
                    ops.Add(OpcodeType.OP_0);
                else
                    ops.Add(Op.GetPushOp(sig.ToBytes()));
            }
            return new Script(ops);
        }
    }

    public class PayToScriptHashSigParameters
    {
        public Script RedeemScript
        {
            get;
            set;
        }

        public byte[][] Pushes
        {
            get;
            set;
        }

        public TransactionSignature[] GetMultisigSignatures(Network network)
        {
            return PayToMultiSigTemplate.Instance.ExtractScriptSigParameters(network, new Script(this.Pushes.Select(p => Op.GetPushOp(p)).ToArray()));
        }
    }

    //https://github.com/bitcoin/bips/blob/master/bip-0016.mediawiki
    public class PayToScriptHashTemplate : ScriptTemplate
    {
        private static readonly PayToScriptHashTemplate _Instance = new PayToScriptHashTemplate();

        public static PayToScriptHashTemplate Instance
        {
            get
            {
                return _Instance;
            }
        }

        public Script GenerateScriptPubKey(ScriptId scriptId)
        {
            return new Script(
                OpcodeType.OP_HASH160,
                Op.GetPushOp(scriptId.ToBytes()),
                OpcodeType.OP_EQUAL);
        }

        public Script GenerateScriptPubKey(Script scriptPubKey)
        {
            return GenerateScriptPubKey(scriptPubKey.Hash);
        }

        protected override bool FastCheckScriptPubKey(Script scriptPubKey, out bool needMoreCheck)
        {
            byte[] bytes = scriptPubKey.ToBytes(true);
            needMoreCheck = false;
            return
                   bytes.Length == 23 &&
                   bytes[0] == (byte)OpcodeType.OP_HASH160 &&
                   bytes[1] == 0x14 &&
                   bytes[22] == (byte)OpcodeType.OP_EQUAL;
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            return true;
        }

        public PayToScriptHashSigParameters ExtractScriptSigParameters(Network network, Script scriptSig)
        {
            return ExtractScriptSigParameters(network, scriptSig, null as Script);
        }

        public PayToScriptHashSigParameters ExtractScriptSigParameters(Network network, Script scriptSig, ScriptId expectedScriptId)
        {
            if (expectedScriptId == null)
                return ExtractScriptSigParameters(network, scriptSig, null as Script);
            return ExtractScriptSigParameters(network, scriptSig, expectedScriptId.ScriptPubKey);
        }

        public PayToScriptHashSigParameters ExtractScriptSigParameters(Network network, Script scriptSig, Script scriptPubKey)
        {
            Op[] ops = scriptSig.ToOps().ToArray();
            Op[] ops2 = scriptPubKey == null ? null : scriptPubKey.ToOps().ToArray();
            if (!CheckScriptSigCore(network, scriptSig, ops, scriptPubKey, ops2))
                return null;

            var result = new PayToScriptHashSigParameters();
            result.RedeemScript = Script.FromBytesUnsafe(ops[ops.Length - 1].PushData);
            result.Pushes = ops.Take(ops.Length - 1).Select(o => o.PushData).ToArray();
            return result;
        }

        private Script GenerateScriptSig(byte[][] pushes, Script redeemScript)
        {
            var ops = new List<Op>();
            foreach (byte[] push in pushes)
                ops.Add(Op.GetPushOp(push));
            ops.Add(Op.GetPushOp(redeemScript.ToBytes(true)));
            return new Script(ops);
        }

        public Script GenerateScriptSig(Network network, ECDSASignature[] signatures, Script redeemScript)
        {
            return GenerateScriptSig(network, signatures.Select(s => new TransactionSignature(s, SigHash.All)).ToArray(), redeemScript);
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            Op[] ops = scriptSigOps;
            if (ops.Length == 0)
                return false;
            if (!scriptSig.IsPushOnly)
                return false;
            if (scriptPubKey != null)
            {
                ScriptId expectedHash = ExtractScriptPubKeyParameters(scriptPubKey);
                if (expectedHash == null)
                    return false;
                if (expectedHash != Script.FromBytesUnsafe(ops[ops.Length - 1].PushData).Hash)
                    return false;
            }

            byte[] redeemBytes = ops[ops.Length - 1].PushData;
            if (redeemBytes.Length > 520)
                return false;
            return Script.FromBytesUnsafe(ops[ops.Length - 1].PushData).IsValid;
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_SCRIPTHASH;
            }
        }

        public ScriptId ExtractScriptPubKeyParameters(Script scriptPubKey)
        {
            bool needMoreCheck;
            if (!FastCheckScriptPubKey(scriptPubKey, out needMoreCheck))
                return null;
            return new ScriptId(scriptPubKey.ToBytes(true).SafeSubarray(2, 20));
        }

        public Script GenerateScriptSig(PayToScriptHashSigParameters parameters)
        {
            return GenerateScriptSig(parameters.Pushes, parameters.RedeemScript);
        }
    }

    public class PayToPubkeyTemplate : ScriptTemplate
    {
        private static readonly PayToPubkeyTemplate _Instance = new PayToPubkeyTemplate();

        public static PayToPubkeyTemplate Instance
        {
            get
            {
                return _Instance;
            }
        }

        public Script GenerateScriptPubKey(PubKey pubkey)
        {
            return GenerateScriptPubKey(pubkey.ToBytes(true));
        }

        public Script GenerateScriptPubKey(byte[] pubkey)
        {
            return new Script(
                    Op.GetPushOp(pubkey),
                    OpcodeType.OP_CHECKSIG
                );
        }

        protected override bool FastCheckScriptPubKey(Script scriptPubKey, out bool needMoreCheck)
        {
            needMoreCheck = false;
            return
                 scriptPubKey.Length > 3 &&
                 PubKey.Check(scriptPubKey.ToBytes(true), 1, scriptPubKey.Length - 2, false) &&
                 scriptPubKey.ToBytes(true)[scriptPubKey.Length - 1] == 0xac;
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            return true;
        }

        public Script GenerateScriptSig(ECDSASignature signature)
        {
            return GenerateScriptSig(new TransactionSignature(signature, SigHash.All));
        }

        public Script GenerateScriptSig(TransactionSignature signature)
        {
            return new Script(
                Op.GetPushOp(signature.ToBytes())
                );
        }

        public TransactionSignature ExtractScriptSigParameters(Network network, Script scriptSig)
        {
            Op[] ops = scriptSig.ToOps().ToArray();
            if (!CheckScriptSigCore(network, scriptSig, ops, null, null))
                return null;

            byte[] data = ops[0].PushData;
            if (!TransactionSignature.ValidLength(data.Length))
                return null;
            try
            {
                return new TransactionSignature(data);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        protected override bool FastCheckScriptSig(Script scriptSig, Script scriptPubKey, out bool needMoreCheck)
        {
            needMoreCheck = true;
            return (67 + 1 <= scriptSig.Length && scriptSig.Length <= 80 + 2) || scriptSig.Length == 9 + 1;
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            Op[] ops = scriptSigOps;
            if (ops.Length != 1)
                return false;
            return ops[0].PushData != null && TransactionSignature.IsValid(network, ops[0].PushData);
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_PUBKEY;
            }
        }

        /// <summary>
        /// Extract the public key or null from the script, perform quick check on pubkey
        /// </summary>
        /// <param name="scriptPubKey"></param>
        /// <returns>The public key</returns>
        public PubKey ExtractScriptPubKeyParameters(Script scriptPubKey)
        {
            bool needMoreCheck;
            if (!FastCheckScriptPubKey(scriptPubKey, out needMoreCheck))
                return null;
            try
            {
                return new PubKey(scriptPubKey.ToBytes(true).SafeSubarray(1, scriptPubKey.Length - 2), true);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Extract the public key or null from the script
        /// </summary>
        /// <param name="scriptPubKey"></param>
        /// <param name="deepCheck">Whether deep checks are done on public key</param>
        /// <returns>The public key</returns>
        public PubKey ExtractScriptPubKeyParameters(Script scriptPubKey, bool deepCheck)
        {
            PubKey result = ExtractScriptPubKeyParameters(scriptPubKey);
            if (result == null || !deepCheck)
                return result;
            return PubKey.Check(result.ToBytes(true), true) ? result : null;
        }
    }

    public class PayToWitPubkeyHashScriptSigParameters : PayToPubkeyHashScriptSigParameters
    {
        public override TxDestination Hash
        {
            get
            {
                return this.PublicKey.WitHash;
            }
        }
    }

    public class PayToPubkeyHashScriptSigParameters : IDestination
    {
        public TransactionSignature TransactionSignature
        {
            get;
            set;
        }

        public PubKey PublicKey
        {
            get;
            set;
        }

        public virtual TxDestination Hash
        {
            get
            {
                return this.PublicKey.Hash;
            }
        }

        #region IDestination Members

        public Script ScriptPubKey
        {
            get
            {
                return this.Hash.ScriptPubKey;
            }
        }

        #endregion IDestination Members
    }

    public class PayToPubkeyHashTemplate : ScriptTemplate
    {
        private static readonly PayToPubkeyHashTemplate _Instance = new PayToPubkeyHashTemplate();

        public static PayToPubkeyHashTemplate Instance
        {
            get
            {
                return _Instance;
            }
        }

        public Script GenerateScriptPubKey(BitcoinPubKeyAddress address)
        {
            if (address == null)
                throw new ArgumentNullException("address");
            return GenerateScriptPubKey(address.Hash);
        }

        public Script GenerateScriptPubKey(PubKey pubKey)
        {
            if (pubKey == null)
                throw new ArgumentNullException("pubKey");
            return GenerateScriptPubKey(pubKey.Hash);
        }

        public Script GenerateScriptPubKey(KeyId pubkeyHash)
        {
            return new Script(
                    OpcodeType.OP_DUP,
                    OpcodeType.OP_HASH160,
                    Op.GetPushOp(pubkeyHash.ToBytes()),
                    OpcodeType.OP_EQUALVERIFY,
                    OpcodeType.OP_CHECKSIG
                );
        }

        public Script GenerateScriptSig(TransactionSignature signature, PubKey publicKey)
        {
            if (publicKey == null)
                throw new ArgumentNullException("publicKey");
            return new Script(
                signature == null ? OpcodeType.OP_0 : Op.GetPushOp(signature.ToBytes()),
                Op.GetPushOp(publicKey.ToBytes())
                );
        }

        protected override bool FastCheckScriptPubKey(Script scriptPubKey, out bool needMoreCheck)
        {
            byte[] bytes = scriptPubKey.ToBytes(true);
            needMoreCheck = false;
            return bytes.Length == 25 &&
                   bytes[0] == (byte)OpcodeType.OP_DUP &&
                   bytes[1] == (byte)OpcodeType.OP_HASH160 &&
                   bytes[2] == 0x14 &&
                   bytes[24] == (byte)OpcodeType.OP_CHECKSIG;
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            return true;
        }

        public KeyId ExtractScriptPubKeyParameters(Script scriptPubKey)
        {
            bool needMoreCheck;
            if (!FastCheckScriptPubKey(scriptPubKey, out needMoreCheck))
                return null;
            return new KeyId(scriptPubKey.ToBytes(true).SafeSubarray(3, 20));
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            Op[] ops = scriptSigOps;
            if (ops.Length != 2)
                return false;
            return ops[0].PushData != null &&
                   ((ops[0].Code == OpcodeType.OP_0) || TransactionSignature.IsValid(network, ops[0].PushData, ScriptVerify.None)) &&
                   ops[1].PushData != null && PubKey.Check(ops[1].PushData, false);
        }

        public bool CheckScriptSig(Network network, Script scriptSig)
        {
            return CheckScriptSig(network, scriptSig, null);
        }

        public PayToPubkeyHashScriptSigParameters ExtractScriptSigParameters(Network network, Script scriptSig)
        {
            Op[] ops = scriptSig.ToOps().ToArray();
            if (!CheckScriptSigCore(network, scriptSig, ops, null, null))
                return null;
            try
            {
                return new PayToPubkeyHashScriptSigParameters()
                {
                    TransactionSignature = ops[0].Code == OpcodeType.OP_0 ? null : new TransactionSignature(ops[0].PushData),
                    PublicKey = new PubKey(ops[1].PushData, true),
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public Script GenerateScriptSig(PayToPubkeyHashScriptSigParameters parameters)
        {
            return GenerateScriptSig(parameters.TransactionSignature, parameters.PublicKey);
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_PUBKEYHASH;
            }
        }
    }

    public abstract class ScriptTemplate
    {
        public virtual bool CheckScriptPubKey(Script scriptPubKey)
        {
            if (scriptPubKey == null)
                throw new ArgumentNullException("scriptPubKey");

            bool needMoreCheck;
            bool result = FastCheckScriptPubKey(scriptPubKey, out needMoreCheck);
            if (needMoreCheck)
            {
                result &= CheckScriptPubKeyCore(scriptPubKey, scriptPubKey.ToOps().ToArray());
            }

            return result;
        }

        protected virtual bool FastCheckScriptPubKey(Script scriptPubKey, out bool needMoreCheck)
        {
            needMoreCheck = true;
            return true;
        }

        protected abstract bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps);

        public virtual bool CheckScriptSig(Network network, Script scriptSig, Script scriptPubKey)
        {
            if (scriptSig == null)
                throw new ArgumentNullException("scriptSig");
            bool needMoreCheck;
            bool result = FastCheckScriptSig(scriptSig, scriptPubKey, out needMoreCheck);
            if (needMoreCheck)
            {
                result &= CheckScriptSigCore(network, scriptSig, scriptSig.ToOps().ToArray(), scriptPubKey, scriptPubKey == null ? null : scriptPubKey.ToOps().ToArray());
            }
            return result;
        }

        protected virtual bool FastCheckScriptSig(Script scriptSig, Script scriptPubKey, out bool needMoreCheck)
        {
            needMoreCheck = true;
            return true;
        }

        protected abstract bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps);

        public Script GenerateScriptSig(Op[] ops, Script redeemScript)
        {
            Op pushScript = Op.GetPushOp(redeemScript._Script);
            return new Script(ops.Concat(new[] { pushScript }));
        }

        public Script GenerateScriptSig(Network network, TransactionSignature[] signatures, Script redeemScript)
        {
            var ops = new List<Op>();
            var multiSigTemplate = new PayToMultiSigTemplate();
            bool multiSig = multiSigTemplate.CheckScriptPubKey(redeemScript);
            if (multiSig)
                ops.Add(OpcodeType.OP_0);
            foreach (TransactionSignature sig in signatures)
            {
                ops.Add(sig == null ? OpcodeType.OP_0 : Op.GetPushOp(sig.ToBytes()));
            }
            return GenerateScriptSig(ops.ToArray(), redeemScript);
        }

        public abstract TxOutType Type
        {
            get;
        }
    }

    public class PayToWitPubKeyHashTemplate : PayToWitTemplate
    {
        private static PayToWitPubKeyHashTemplate _Instance;

        public new static PayToWitPubKeyHashTemplate Instance
        {
            get
            {
                return _Instance = _Instance ?? new PayToWitPubKeyHashTemplate();
            }
        }

        public Script GenerateScriptPubKey(PubKey pubKey)
        {
            if (pubKey == null)
                throw new ArgumentNullException("pubKey");
            return GenerateScriptPubKey(pubKey.WitHash);
        }

        public Script GenerateScriptPubKey(WitKeyId pubkeyHash)
        {
            return pubkeyHash.ScriptPubKey;
        }

        public WitScript GenerateWitScript(TransactionSignature signature, PubKey publicKey)
        {
            if (publicKey == null)
                throw new ArgumentNullException("publicKey");
            return new WitScript(
                signature == null ? OpcodeType.OP_0 : Op.GetPushOp(signature.ToBytes()),
                Op.GetPushOp(publicKey.ToBytes())
                );
        }

        public Script GenerateScriptPubKey(BitcoinWitPubKeyAddress address)
        {
            if (address == null)
                throw new ArgumentNullException("address");
            return GenerateScriptPubKey(address.Hash);
        }

        public override bool CheckScriptPubKey(Script scriptPubKey)
        {
            if (scriptPubKey == null)
                throw new ArgumentNullException("scriptPubKey");
            byte[] bytes = scriptPubKey.ToBytes(true);
            return bytes.Length == 22 && bytes[0] == 0 && bytes[1] == 20;
        }

        public new WitKeyId ExtractScriptPubKeyParameters(Network network, Script scriptPubKey)
        {
            if (!CheckScriptPubKey(scriptPubKey))
                return null;
            var data = new byte[20];
            Array.Copy(scriptPubKey.ToBytes(true), 2, data, 0, 20);
            return new WitKeyId(data);
        }

        public PayToWitPubkeyHashScriptSigParameters ExtractWitScriptParameters(Network network, WitScript witScript)
        {
            if (!CheckWitScriptCore(network, witScript))
                return null;
            try
            {
                return new PayToWitPubkeyHashScriptSigParameters()
                {
                    TransactionSignature = (witScript[0].Length == 1 && witScript[0][0] == 0) ? null : new TransactionSignature(witScript[0]),
                    PublicKey = new PubKey(witScript[1], true),
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private bool CheckWitScriptCore(Network network, WitScript witScript)
        {
            return witScript.PushCount == 2 &&
                   ((witScript[0].Length == 1 && witScript[0][0] == 0) || (TransactionSignature.IsValid(network, witScript[0], ScriptVerify.None))) &&
                   PubKey.Check(witScript[1], false);
        }

        public WitScript GenerateWitScript(PayToWitPubkeyHashScriptSigParameters parameters)
        {
            return GenerateWitScript(parameters.TransactionSignature, parameters.PublicKey);
        }
    }

    public class PayToWitScriptHashTemplate : PayToWitTemplate
    {
        private static PayToWitScriptHashTemplate _Instance;

        public new static PayToWitScriptHashTemplate Instance
        {
            get
            {
                return _Instance = _Instance ?? new PayToWitScriptHashTemplate();
            }
        }

        public Script GenerateScriptPubKey(WitScriptId scriptHash)
        {
            return scriptHash.ScriptPubKey;
        }

        public WitScript GenerateWitScript(Script scriptSig, Script redeemScript)
        {
            if (redeemScript == null)
                throw new ArgumentNullException("redeemScript");
            if (scriptSig == null)
                throw new ArgumentNullException("scriptSig");
            if (!scriptSig.IsPushOnly)
                throw new ArgumentException("The script sig should be push only", "scriptSig");
            scriptSig = scriptSig + Op.GetPushOp(redeemScript.ToBytes(true));
            return new WitScript(scriptSig);
        }

        public WitScript GenerateWitScript(Op[] scriptSig, Script redeemScript)
        {
            if (redeemScript == null)
                throw new ArgumentNullException("redeemScript");
            if (scriptSig == null)
                throw new ArgumentNullException("scriptSig");
            return GenerateWitScript(new Script(scriptSig), redeemScript);
        }

        public Script GenerateScriptPubKey(BitcoinWitScriptAddress address)
        {
            if (address == null)
                throw new ArgumentNullException("address");
            return GenerateScriptPubKey(address.Hash);
        }

        public override bool CheckScriptPubKey(Script scriptPubKey)
        {
            if (scriptPubKey == null)
                throw new ArgumentNullException("scriptPubKey");
            byte[] bytes = scriptPubKey.ToBytes(true);
            return bytes.Length == 34 && bytes[0] == 0 && bytes[1] == 32;
        }

        public new WitScriptId ExtractScriptPubKeyParameters(Network network, Script scriptPubKey)
        {
            if (!CheckScriptPubKey(scriptPubKey))
                return null;
            var data = new byte[32];
            Array.Copy(scriptPubKey.ToBytes(true), 2, data, 0, 32);
            return new WitScriptId(data);
        }

        /// <summary>
        /// Extract witness redeem from WitScript
        /// </summary>
        /// <param name="witScript">Witscript to extract information from</param>
        /// <param name="expectedScriptId">Expected redeem hash</param>
        /// <returns>The witness redeem</returns>
        public Script ExtractWitScriptParameters(WitScript witScript, WitScriptId expectedScriptId = null)
        {
            if (witScript.PushCount == 0)
                return null;
            byte[] last = witScript.GetUnsafePush(witScript.PushCount - 1);
            var redeem = new Script(last);
            if (expectedScriptId != null)
            {
                if (expectedScriptId != redeem.WitHash)
                    return null;
            }
            return redeem;
        }
    }

    public class WitProgramParameters
    {
        public OpcodeType Version
        {
            get;
            set;
        }

        public byte[] Program
        {
            get;
            set;
        }
    }

    public class PayToWitTemplate : ScriptTemplate
    {
        private static PayToWitTemplate _Instance;

        public static PayToWitTemplate Instance
        {
            get
            {
                return _Instance = _Instance ?? new PayToWitTemplate();
            }
        }

        public Script GenerateScriptPubKey(OpcodeType segWitVersion, byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (!ValidSegwitVersion((byte)segWitVersion))
                throw new ArgumentException("Segwit version must be from OP_0 to OP_16", "segWitVersion");
            return new Script(segWitVersion, Op.GetPushOp(data));
        }

        public override bool CheckScriptSig(Network network, Script scriptSig, Script scriptPubKey)
        {
            if (scriptSig == null)
                throw new ArgumentNullException("scriptSig");
            return scriptSig.Length == 0;
        }

        public override bool CheckScriptPubKey(Script scriptPubKey)
        {
            if (scriptPubKey == null)
                throw new ArgumentNullException("scriptPubKey");
            byte[] bytes = scriptPubKey.ToBytes(true);
            if (bytes.Length < 4 || bytes.Length > 34)
            {
                return false;
            }
            byte version = bytes[0];
            if (!ValidSegwitVersion(version))
                return false;
            return bytes[1] + 2 == bytes.Length;
        }

        public static bool ValidSegwitVersion(byte version)
        {
            return version == 0 || ((byte)OpcodeType.OP_1 <= version && version <= (byte)OpcodeType.OP_16);
        }

        public TxDestination ExtractScriptPubKeyParameters(Network network, Script scriptPubKey)
        {
            if (!CheckScriptPubKey(scriptPubKey))
                return null;
            Op[] ops = scriptPubKey.ToOps().ToArray();
            if (ops.Length != 2 || ops[1].PushData == null)
                return null;
            if (ops[0].Code == OpcodeType.OP_0)
            {
                if (ops[1].PushData.Length == 20)
                    return new WitKeyId(ops[1].PushData);
                if (ops[1].PushData.Length == 32)
                    return new WitScriptId(ops[1].PushData);
            }
            return null;
        }

        public WitProgramParameters ExtractScriptPubKeyParameters2(Network network, Script scriptPubKey)
        {
            if (!CheckScriptPubKey(scriptPubKey))
                return null;
            Op[] ops = scriptPubKey.ToOps().ToArray();
            if (ops.Length != 2 || ops[1].PushData == null)
                return null;
            return new WitProgramParameters()
            {
                Version = ops[0].Code,
                Program = ops[1].PushData
            };
        }

        public override TxOutType Type
        {
            get
            {
                return TxOutType.TX_SEGWIT;
            }
        }

        protected override bool CheckScriptPubKeyCore(Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            throw new NotImplementedException();
        }

        protected override bool CheckScriptSigCore(Network network, Script scriptSig, Op[] scriptSigOps, Script scriptPubKey, Op[] scriptPubKeyOps)
        {
            throw new NotImplementedException();
        }
    }
}