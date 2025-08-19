using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using TonSdk.Contracts.Wallet;
using TonSdk.Core;
using TonSdk.Core.Block;
using TonSdk.Core.Boc;
using TonSdk.Core.Crypto;

namespace TonSdk.Contracts
{
    public struct WalletV5Actions 
    {
        public ActionsOrCell<OutAction[]> Wallet;
        public ActionsOrCell<IWalletV5ExtendedAction[]> Extended;
    }

    public class ActionsOrCell<T>
    {
        public T Actions;
        public Cell Cell = null;
        public bool IsActions => Actions != null;
        public bool IsCell => Cell != null;
    }

    public interface IWalletV5ExtendedAction {}

    public struct ExtensionAdd : IWalletV5ExtendedAction
    {
        public Address Address;
    }

    public struct ExtensionRemove : IWalletV5ExtendedAction
    {
        public Address Address;
    }

    public struct SetSignatureAuth : IWalletV5ExtendedAction
    {
        public bool Allowed;
    }

    public enum WalletV5Version
    {
        V5R1 = 0,
    }

    public enum WalletV5Prefixes
    {
        SignedInternal = 0x73696E74,
        SignedExternal = 0x7369676E,
    }

    public class WalletV5Options
    {
        public bool SignatureAllowed = true;
        public uint Seqno = 0;
        public int Workchain = 0;
        public WalletIdV5R1<IWalletIdV5R1Context> WalletId = new WalletIdV5R1<IWalletIdV5R1Context> 
        {
            NetworkGlobalId = -239,
            Context = new WalletIdV5R1ClientContext 
            {
                Version = WalletV5Version.V5R1,
                SubwalletId = 0
            }
        };
        public byte[] PublicKey;
        public Dictionary<BigInteger, BigInteger> Extensions = new Dictionary<BigInteger, BigInteger>();
    }

    public class WalletIdV5R1<C> where C : IWalletIdV5R1Context
    {
        public int NetworkGlobalId = -239;
        public C Context;
    }

    public interface IWalletIdV5R1Context {}

    public class WalletIdV5R1ClientContext : IWalletIdV5R1Context
    {
        public WalletV5Version Version = WalletV5Version.V5R1;
        public uint SubwalletId = WalletTraits.SUBWALLET_ID;
    }

    public class WalletIdV5R1CustomContext : IWalletIdV5R1Context
    {
        public int Value;
    }

    public class WalletV5 : WalletBase
    {
        private WalletV5Options _options;

        public WalletV5(WalletV5Options opt)
        {
            _code = Cell.From(WalletSources.V5R1);
            _options = opt;
            _publicKey = opt.PublicKey;
            _stateInit = buildStateInit();
            _address = new Address(opt.Workchain, _stateInit);
        }

        protected sealed override StateInit buildStateInit()
        {
            var dict = new HashmapE<uint, BigInteger>(new HashmapOptions<uint, BigInteger> {
                KeySize = 256,
                Serializers = new HashmapSerializers<uint, BigInteger> {
                    Key = k => new BitsBuilder(32).StoreUInt(k, 32).Build(),
                    Value = v => new CellBuilder().StoreBytes(v.ToByteArray()).Build()
                },
                Deserializers = new HashmapDeserializers<uint, BigInteger> {
                    Key = kb => (uint)kb.Parse().LoadUInt(32),
                    Value = v => BigInteger.Parse(Encoding.UTF8.GetString(v.Parse().LoadBytes(256)))
                }
            });

            var data = new CellBuilder()
                .StoreBit(_options.SignatureAllowed)
                .StoreUInt(_options.Seqno, 32)
                .StoreInt(GetSerializedWalletId(_options.WalletId, _options.Workchain), 32)
                .StoreBytes(_options.PublicKey)
                .StoreDict(dict)
                .Build();

            return new StateInit(new StateInitOptions { Code = _code, Data = data });
        }

        public ExternalInMessage CreateDeployMessage(byte[] privateKey = null) 
        {
            return new ExternalInMessage(new ExternalInMessageOptions {
                Info = new ExtInMsgInfo(new ExtInMsgInfoOptions { Dest = Address, }),
                Body = PackMessage(false, privateKey: privateKey),
                StateInit = _stateInit
            });
        }

        public ExternalInMessage CreateTransferMessage(WalletTransfer[] transfers, uint seqno, byte[] privateKey = null, int timeout = 60)
        {
            if(transfers.Length == 0 || transfers.Length > 255)
                throw new Exception("WalletV5: can make only 1 to 255 transfers per operation.");

            var actions = new OutAction[transfers.Length];
            for (var i = 0; i < transfers.Length; i++)
            {
                var transfer = transfers[i];
                var action = new ActionSendMsg(new ActionSendMsgOptions
                {
                    Mode = transfer.Mode,
                    OutMsg = transfer.Message
                });
                actions[i] = action;
            }

            return new ExternalInMessage(new ExternalInMessageOptions {
                Info = new ExtInMsgInfo(new ExtInMsgInfoOptions { Dest = Address }),
                Body = PackMessage(false, timeout, new WalletV5Actions { Wallet = new ActionsOrCell<OutAction[]> { Actions = actions } }, seqno, privateKey),
                StateInit = _stateInit
            });
        }

        private CellSlice PackExtensionActions(ActionsOrCell<IWalletV5ExtendedAction[]> actions)
        {
            var body = new CellBuilder();
        
            if(actions.IsCell)
                body.StoreCellSlice(actions.Cell.Parse());
            else if(actions.IsActions)
            {
                var cell = actions.Actions
                    .Reverse()
                    .Aggregate(new Cell(new Bits(0), Array.Empty<Cell>()), (cur, action) =>
                    {
                        var ds = action switch
                        {
                            ExtensionAdd a => new CellBuilder().StoreUInt(2, 8).StoreAddress(a.Address),
                            ExtensionRemove r => new CellBuilder().StoreUInt(3, 8).StoreAddress(r.Address),
                            SetSignatureAuth s => new CellBuilder().StoreUInt(4, 8).StoreBit(s.Allowed),
                            _ => throw new Exception("Invalid action type"),
                        };

                        return ds
                            .StoreRef(cur)
                            .Build();
                    });

                body.StoreCellSlice(cell.Parse());
            }
            else
                throw new Exception("WalletV5: actions are not provided");

            return body.Build().Parse();
        }

        private Cell PackMessage(bool isInternal, int timeout = 60, WalletV5Actions? actions = null, uint seqno = 0, byte[]? privateKey = null)
        {
            var body = new CellBuilder()
                .StoreUInt(isInternal ? (uint)WalletV5Prefixes.SignedInternal : (uint)WalletV5Prefixes.SignedExternal, 32)
                .StoreUInt(GetSerializedWalletId(_options.WalletId, _options.Workchain), 32)
                .StoreUInt(DateTimeOffset.Now.ToUnixTimeSeconds() + timeout, 32)
                .StoreUInt(seqno, 32);

            if(actions.HasValue)
            {
                if(actions.Value.Wallet != null)
                    body.StoreOptRef(actions.Value.Wallet.IsActions ? new OutList(new OutListOptions { Actions = actions.Value.Wallet.Actions }).Cell : actions.Value.Wallet.Cell);
                else
                    body.StoreBit(false); // empty out_list

                if(actions.Value.Extended != null)
                {
                    body.StoreBit(true);
                    body.StoreCellSlice(PackExtensionActions(actions.Value.Extended));
                }
                else
                    body.StoreBit(false); // empty out_list
            }

            if(privateKey != null)
            {
                var signature = KeyPair.Sign(body.Build(), privateKey);
                return new CellBuilder()
                    .StoreCellSlice(body.Build().Parse())
                    .StoreBytes(signature)
                    .Build();
            }

            return body.Build();
        }

        private int GetSerializedWalletId(WalletIdV5R1<IWalletIdV5R1Context> walletId, int workchain = 0)
        {
            var serializedContext = new CellBuilder();
            if(walletId.Context is WalletIdV5R1ClientContext clientContext)
            {
                serializedContext
                    .StoreUInt(1, 1)
                    .StoreInt(workchain, 8)
                    .StoreUInt((uint)clientContext.Version, 8)
                    .StoreUInt(clientContext.SubwalletId, 15);
            }
            else if(walletId.Context is WalletIdV5R1CustomContext customContext)
            {
                serializedContext
                    .StoreUInt(0, 1)
                    .StoreUInt(customContext.Value, 31);
            }

            return walletId.NetworkGlobalId ^ (int)serializedContext.Build().Parse().LoadInt(32);
        }
    }
}
