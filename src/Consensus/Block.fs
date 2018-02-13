module Consensus.Block

open Consensus
open Consensus.Types

open MBrace.FsPickler.Combinators

open Consensus
open Consensus
open Consensus
open Infrastructure

[<Literal>]
let Version = 0ul

let TwoPow256 = bigint.Pow (2I, 256)

let MaxTimeInFuture = 2UL * 60UL * 60UL * 1000UL // 2 hours in milliseconds

let genesisParent = {version=Version;parent=Hash.zero;blockNumber=0ul;commitments=Hash.zero;timestamp=0UL;difficulty=0ul;nonce=0UL,0UL}

let pickler = Pickler.auto<Block>

let private (>=>) f1 f2 x = Result.bind f2 (f1 x)

let result = new Result.ResultBuilder<string>()

let private createCommitments txMerkleRoot witnessMerkleRoot acsMerkleRoot rest =
    [ txMerkleRoot; witnessMerkleRoot; acsMerkleRoot; ] @ rest
    
let private computeCommitmentsRoot = MerkleTree.computeRoot  
    
let hash (block:Block) = BlockHeader.hash block.header

let serialize block =   

    Binary.pickle pickler block     

let deserialize block =
    try
        Some (Binary.unpickle pickler block) with
    | _ -> None

let toHex = serialize >> FsBech32.Base16.encode

let fromHex hex = 
    FsBech32.Base16.decode hex
    |> Option.bind deserialize          

let isGenesis chain block = 
    let blockHash = hash block
    
    ChainParameters.getGenesisHash chain = blockHash
        
let getChainWork (prevWork:bigint) header = 
    let target = 
        Difficulty.uncompress header.difficulty
        |> Hash.toBigInt         
        
    let proof = bigint.Divide (TwoPow256, target + 1I)
    
    prevWork + proof
    
let getBlockReward blockNumber = 
    //TODO: implement this method
    50UL * 100000000UL             

let createGenesis chain transactions nonce = 
    let txMerkleRoot = 
        transactions
        |> List.map Transaction.hash
        |> MerkleTree.computeRoot 
        
    let witnessMerkleRoot = 
        transactions
        |> List.map Transaction.witnessHash
        |> MerkleTree.computeRoot    
        
    let acsMerkleRoot = SparseMerkleTree.root ActiveContractSet.empty
    
    let commitments = 
        createCommitments txMerkleRoot witnessMerkleRoot acsMerkleRoot []
        |> computeCommitmentsRoot
        
    let header = 
        {
            version=Version;
            parent=Hash.zero;
            blockNumber=1ul;
            commitments=commitments;
            timestamp=ChainParameters.getGenesisTime chain;
            difficulty=(EMA.create chain).difficulty;
            nonce=nonce;
        }
        
    {header=header;transactions=transactions;commitments=[];txMerkleRoot=txMerkleRoot;witnessMerkleRoot=witnessMerkleRoot;activeContractSetMerkleRoot=acsMerkleRoot}

let createTemplate (parent:BlockHeader) timestamp (ema:EMA.T) acs transactions coinbasePkHash =
    let blockNumber = (parent.blockNumber + 1ul)
    let reward = getBlockReward blockNumber
    
    let coinbase = {
        inputs=[]
        outputs=
            [
                {
                    lock=Coinbase (blockNumber, coinbasePkHash)
                    spend={asset=Constants.Zen;amount=reward}
                }
            ]
        contract = None
        witnesses=[]
    }

    let transactions = coinbase :: transactions
 
    let txMerkleRoot = 
        transactions
        |> List.map Transaction.hash
        |> MerkleTree.computeRoot                
        
    let witnessMerkleRoot = 
        transactions
        |> List.map Transaction.witnessHash
        |> MerkleTree.computeRoot
        
    let acsMerkleRoot = SparseMerkleTree.root acs
        
    let parentHash = BlockHeader.hash parent        
                
    // TODO: add utxo commitments   
    let commitments = 
        createCommitments txMerkleRoot witnessMerkleRoot acsMerkleRoot []
        |> computeCommitmentsRoot
    
    let header = 
        {
            version=Version;
            parent=parentHash;
            blockNumber=blockNumber;
            commitments=commitments;
            timestamp=timestamp;
            difficulty=ema.difficulty;
            nonce=0UL,0UL;
        }
        
    {header=header;transactions=transactions;commitments=[];txMerkleRoot=txMerkleRoot;witnessMerkleRoot=witnessMerkleRoot;activeContractSetMerkleRoot=acsMerkleRoot}

let validate chain =     
    let checkTxNotEmpty (block:Block) =
        if List.isEmpty block.transactions then Error "transactions is empty" else Ok block
            
    let checkHeader (block:Block) =        
        BlockHeader.validate chain block.header
        |> Result.map (fun _ -> block)  
        
    let checkCoinbase (block:Block) = 
        if isGenesis chain block then 
            Ok block
        else
            let coinbase = List.head block.transactions
            
            match TransactionValidation.validateCoinbase block.header.blockNumber coinbase with
            | Error error -> Error <| sprintf "Block failed coinbase validation due to %A" error
            | Ok _ ->
                // checking the total reward
                let totals = List.fold (fun totals output ->
                    let amount = defaultArg (Map.tryFind output.spend.asset totals) 0UL            
                    Map.add output.spend.asset (output.spend.amount + amount) totals) Map.empty coinbase.outputs 
                    
                let reward = getBlockReward block.header.blockNumber 
                let totalZen = defaultArg (Map.tryFind Constants.Zen totals) 0UL
                
                if totalZen <> reward then
                    Error "block reward is incorrect"
                else
                    // TODO: we don't have fees yet, so anything else other than zen is not allowed
                    if Map.exists (fun asset _ -> asset <> Constants.Zen) totals then     
                        Error "reward can only be in Zen asset"
                    else
                        Ok block
                                                 
    let checkTxBasic (block:Block) = result {
        // skip if genesis block
        if isGenesis chain block then
            return block
        else
            let withoutCoinbase = List.tail block.transactions
        
            // Fail if validateBasic fails on any transaction in the block.
            for tx in withoutCoinbase do
                let! _ =
                    TransactionValidation.validateBasic tx 
                    |> Result.mapError (sprintf "transaction %A failed validation due to %A" (Transaction.hash tx) )
                ()
            return block
    }
                    
    let checkCommitments (block:Block) = 
        let txMerkleRoot = 
            block.transactions
            |> List.map Transaction.hash            
            |> MerkleTree.computeRoot
                    
        let witnessMerkleRoot = 
            block.transactions
            |> List.map Transaction.witnessHash
            |> MerkleTree.computeRoot 
    
        if txMerkleRoot = block.txMerkleRoot && witnessMerkleRoot = block.witnessMerkleRoot then    
            let commitments = 
                createCommitments block.txMerkleRoot block.witnessMerkleRoot block.activeContractSetMerkleRoot block.commitments
                |> computeCommitmentsRoot
            
            if commitments = block.header.commitments then
                Ok block
            else
                Error "commitments mismatch"
        else
            Error "commitments mismatch"
   
    checkTxNotEmpty    
    >=> checkHeader
    >=> checkCoinbase
    >=> checkTxBasic
    >=> checkCommitments

/// Apply block to UTXO and ACS, operation can fail
let connect chain getUTXO contractsPath parent timestamp set acs ema =    
    let checkBlockNumber (block:Block) = 
        if parent.blockNumber + 1ul <> block.header.blockNumber then
            Error "blockNumber mismatch"
        else Ok block 

    let checkDifficulty (block:Block) =                       
        let nextEma = EMA.add chain block.header.timestamp ema          
        if block.header.difficulty <> ema.difficulty then 
            Error "incorrect proof of work" 
        elif isGenesis chain block then 
            Ok (block,nextEma)
        elif block.header.timestamp <= EMA.earliest ema then
            Error "block's timestamp is too early"
        elif block.header.timestamp > timestamp + MaxTimeInFuture then
            Error "block timestamp too far in the future"    
        else
            Ok (block,nextEma)  
                                                 
    let checkTxInputs (block,ema) =
        if isGenesis chain block then
            let set = List.fold (fun set tx ->
                let txHash = (Transaction.hash tx)
                UtxoSet.handleTransaction getUTXO txHash tx set) set block.transactions
            Ok (block,set,acs,ema)
        else        
            let coinbase = List.head block.transactions
            let withoutCoinbase = List.tail block.transactions 
            
            let set = UtxoSet.handleTransaction getUTXO (Transaction.hash coinbase) coinbase set
                       
            List.fold (fun state tx->
                match state with
                | Error e -> Error e
                | Ok (block,set,acs,ema) -> 
                    let txHash = (Transaction.hash tx) 
                
                    match TransactionValidation.validateInContext getUTXO contractsPath (block.header.blockNumber - 1ul) acs set txHash tx with
                    | Error err -> Error (sprintf "transactions failed inputs validation due to %A" err)
                    | Ok (_,acs) -> 
                        let set = UtxoSet.handleTransaction getUTXO txHash tx set
                            
                        Ok (block,set,acs,ema) 
                    ) (Ok (block,set,acs,ema)) withoutCoinbase
                    
    let checkCommitments (block,set,acs,ema) =                                   
        let acsMerkleRoot = SparseMerkleTree.root acs
        
        // we already validated txMerkleRoot and witness merkle root at the basic validation, re-calculate with acsMerkleRoot
        let commitments = 
            createCommitments block.txMerkleRoot block.witnessMerkleRoot acsMerkleRoot block.commitments 
            |> computeCommitmentsRoot
        
        // We ignore the known commitments in the block as we already calculated them
        // Only check that the final commitment is correct                 
        if commitments = block.header.commitments then                
            Ok (block,set,acs,ema) 
        else
            Error "commitments mismatch"

    checkBlockNumber
    >=> checkDifficulty    
    >=> checkTxInputs
    >=> checkCommitments
       
    
                
