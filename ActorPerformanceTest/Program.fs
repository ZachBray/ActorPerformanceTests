open System
open System.Diagnostics
open Microsoft.FSharp.Control

/// Code to measure the number of messages the
/// agent can process per second on a number of threads.
let test name f g =
   let test countPerThread =
      let threadCount = System.Environment.ProcessorCount
      let msgCount = threadCount * countPerThread
      let incrementers =
         Array.init threadCount (fun _ ->
            async { for i = 1 to countPerThread do f 100L })
      let watch = Stopwatch.StartNew()
      let finalCount =
         async {
            do! incrementers |> Async.Parallel |> Async.Ignore
            return g()
         } |> Async.RunSynchronously
      let expectedCount = int64 countPerThread * int64 threadCount * 100L 
      if finalCount <> expectedCount then
         failwith "Didn't work!"
      watch.Stop()
      int (float msgCount / watch.Elapsed.TotalSeconds)
      
   // Warm up!
   test 10000 |> ignore<int>
   System.GC.Collect()
   // Real test
   let msgsPerSecond = test 500000
   printfn "%s processed %i msgs/sec" name msgsPerSecond


type CounterMsg =
   | Add of int64
   | GetAndReset of (int64 -> unit)

let vanillaCounter = 
   MailboxProcessor.Start <| fun inbox ->
      let rec loop count = async {
         let! msg = inbox.Receive()
         match msg with
         | Add n -> return! loop (count + n)
         | GetAndReset reply ->
            reply count
            return! loop 0L
      }
      loop 0L

test "Vanilla Actor (MailboxProcessor)" 
   (fun i -> vanillaCounter.Post <| Add i) 
   (fun () -> vanillaCounter.PostAndReply(fun channel -> GetAndReset channel.Reply))


open System.Threading
open System.Collections.Concurrent

type 'a ISimpleActor =
   inherit IDisposable
   abstract Post : msg:'a -> unit
   abstract PostAndReply<'b> : msgFactory:(('b -> unit) -> 'a) -> 'b

type 'a SimpleMailbox() =
   let msgs = ConcurrentQueue<'a>()
   let onMsg = new AutoResetEvent(false)

   member __.Receive() =
      let rec await() = async {
         let mutable value = Unchecked.defaultof<_>
         let hasValue = msgs.TryDequeue(&value)
         if hasValue then return value
         else 
            let! _ = Async.AwaitWaitHandle onMsg
            return! await()        
      }
      await()

   member __.Post msg = 
      msgs.Enqueue msg
      onMsg.Set() |> ignore<bool>

   member __.PostAndReply<'b> msgFactory =
      let value = ref Unchecked.defaultof<'b>
      use onReply = new AutoResetEvent(false) 
      let msg = msgFactory (fun x ->
         value := x
         onReply.Set() |> ignore<bool>
      )
      __.Post msg
      onReply.WaitOne() |> ignore<bool>
      !value

   interface 'a ISimpleActor with
      member __.Post msg = __.Post msg
      member __.PostAndReply msgFactory = __.PostAndReply msgFactory
      member __.Dispose() = onMsg.Dispose()

module SimpleActor =
   let Start f =
      let mailbox = new SimpleMailbox<_>()
      f mailbox |> Async.Start
      mailbox :> _ ISimpleActor

let simpleActor = 
   SimpleActor.Start <| fun inbox ->
      let rec loop count = async {
         let! msg = inbox.Receive()
         match msg with
         | Add n -> return! loop (count + n)
         | GetAndReset reply ->
            reply count
            return! loop 0L
      }
      loop 0L

test "Simple Actor" 
   (fun i -> simpleActor.Post <| Add i) 
   (fun () -> simpleActor.PostAndReply GetAndReset)


type 'a ISharedActor =
   abstract Post : msg:'a -> unit
   abstract PostAndReply<'b> : msgFactory:(('b -> unit) -> 'a) -> 'b

type 'a SharedMailbox() =
   let msgs = ConcurrentQueue<'a>()
   let mutable isStarted = false
   let mutable msgCount = 0
   let mutable react = Unchecked.defaultof<_>
   let mutable currentMessage = Unchecked.defaultof<_>

   let rec execute(isFirst) =

      let inline consumeAndLoop() =
         react currentMessage
         currentMessage <- Unchecked.defaultof<_>
         let newCount = Interlocked.Decrement &msgCount
         if newCount <> 0 then execute false

      if isFirst then consumeAndLoop()
      else
         let hasMessage = msgs.TryDequeue(&currentMessage)
         if hasMessage then consumeAndLoop()
         else 
            Thread.SpinWait 20
            execute false
   
   member __.Receive(callback) = 
      isStarted <- true
      react <- callback

   member __.Post msg =
      while not isStarted do Thread.SpinWait 20
      let newCount = Interlocked.Increment &msgCount
      if newCount = 1 then
         currentMessage <- msg
         // Might want to schedule this call on another thread.
         execute true
      else msgs.Enqueue msg
   
   member __.PostAndReply msgFactory =
      let value = ref Unchecked.defaultof<'b>
      use onReply = new AutoResetEvent(false)
      let msg = msgFactory (fun x ->
         value := x
         onReply.Set() |> ignore<bool>
      )
      __.Post msg
      onReply.WaitOne() |> ignore<bool>
      !value


   interface 'a ISharedActor with
      member __.Post msg = __.Post msg
      member __.PostAndReply msgFactory = __.PostAndReply msgFactory

module SharedActor =
   let Start f =
      let mailbox = new SharedMailbox<_>()
      f mailbox
      mailbox :> _ ISharedActor

let sharedActor = 
   SharedActor.Start <| fun inbox ->
      let rec loop count =
         inbox.Receive(fun msg ->
            match msg with
            | Add n -> loop (count + n)
            | GetAndReset reply ->
               reply count
               loop 0L)
      loop 0L

test "Shared Actor" 
   (fun i -> sharedActor.Post <| Add i) 
   (fun () -> sharedActor.PostAndReply GetAndReset)

Console.ReadLine() |> ignore

