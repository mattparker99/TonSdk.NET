---
description: TonSdk.Contracts.Wallet
---

# Wallet V5

`WalletV5` is class to work with Wallet v5, what includes only `1` revision for now.

Source code of this contract published here https://github.com/ton-blockchain/wallet-contract-v5/tree/main

To create `WalletV5` instance you can use class constructor:

```csharp
// create new mnemonic or use existing
Mnemonic mnemonic = new Mnemonic();

// create wallet v5 options
WalletV5Options optionsV5 = new WalletV5Options()
{
    PublicKey = mnemonic.Keys.PublicKey,

    /*
        use WalletId option to pass subwallet id
    */
    WalletId = new WalletIdV5R1<IWalletIdV5R1Context> 
    {
        NetworkGlobalId = -239, // mainnet
        Context = new WalletIdV5R1ClientContext 
        {
            Version = WalletV5Version.V5R1,
            SubwalletId = 0 // subwallet id
        }
    };
};

WalletV5 wallet = new WalletV5(optionsV5);
```



You can create deploy message using `CreateDeployMessage` method:

```csharp
WalletV5 wallet = new WalletV5(optionsV5);

// create deploy message and sign it with private key
var deployMessage = wallet.CreateDeployMessage(mnemonic.Keys.PrivateKey);

Cell deployBoc = deployMessage.Cell;

// send this message via TonClient,
// for example, await tonClient.SendBoc(deployBoc);
```



Also you can create transfer message using `CreateTransferMessage` method:

```csharp
Address destination = new Address("/* destination address */");
Coins amount = new Coins(1); // 1 TON
string comment = "Hello TON!";

WalletV5 walletV5 = new WalletV5(options);

// create transaction body query + comment
Cell body = new CellBuilder()
    .StoreUInt(0, 32)
    .StoreString(comment)
    .Build();

// getting seqno using tonClient
uint? seqno = await tonClient.Wallet.GetSeqno(walletV5.Address);

// create transfer message and sign it
ExternalInMessage message = walletV5.CreateTransferMessage(new[]
{
    new WalletTransfer
    {
        Message = new InternalMessage(new InternalMessageOptions
        {
            Info = new IntMsgInfo(new IntMsgInfoOptions
            {
                Dest = destination,
                Value = amount,
                Bounce = true // make bounceable message
            }),
            Body = body
        }),
        Mode = 1 + 2 // message mode, 
        /*  !!! WARNING !!! always use +2 flag 
        *   in addition to message mode
        *
        *   otherwise it will be transaction error 
        *   without it
        */
    }
}, seqno ?? 0, mnemonic.Keys.PrivateKey);

// get boc
Cell boc = message.Cell;

// send this message via TonClient,
// for example, await tonClient.SendBoc(boc);
```

Also, the useful feature is the batch send. `WalletV5` can handle up to 255 out actions:
```csharp
WalletTransfer transfer = new WalletTransfer
{
    Message = new InternalMessage(new InternalMessageOptions
    {
        Info = new IntMsgInfo(new IntMsgInfoOptions
        {
            Dest = destination,
            Value = amount,
            Bounce = true // make bounceable message
        }),
        Body = body
    }),
    Mode = 1 + 2 // message mode, 
}

List<WalletTransfer> transfers = new List<WalletTransfer>();
for(var i = 0; i < 255; i++) 
{
    transfers.Add(transfer);
}

var message = wallet.CreateTransferMessage(transfers.ToArray(), seqno ?? 0, mnemonic.Keys.PrivateKey);
```