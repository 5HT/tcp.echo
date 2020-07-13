open System
open System.IO
open System.Linq
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Runtime.Serialization
open System.Security.Cryptography
 
[<DataContract>]
type Time =
    { [<DataMember(Name = "hour")>] mutable Hour : int
      [<DataMember(Name = "minute")>] mutable Minute : int
      [<DataMember(Name = "second")>] mutable Second : int }
    static member New(dt : DateTime) = {Hour = dt.Hour; Minute = dt.Minute; Second = dt.Second}

#nowarn "40"

type Msg =
    | Connect of MailboxProcessor<Time>
    | Disconnect of MailboxProcessor<Time>
    | Tick of Time

let port = 1900
let ipAddress = IPAddress.Loopback.ToString()
let origin = "http://localhost"
let guid6455 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

let startMailboxProcessor ct f = MailboxProcessor.Start(f, cancellationToken = ct)

let calcWSAccept6455 (secWebSocketKey:string) = 
    let ck = secWebSocketKey + guid6455
    let sha1 = SHA1CryptoServiceProvider.Create()
    let hashBytes = ck |> Encoding.ASCII.GetBytes |> sha1.ComputeHash
    let Sec_WebSocket_Accept = hashBytes |> Convert.ToBase64String
    Sec_WebSocket_Accept

let createAcceptString6455 acceptCode =
    "HTTP/1.1 101 Switching Protocols\n" +
    "Upgrade: websocket\n" +
    "Connection: Upgrade\n" +
    "Sec-WebSocket-Accept: " + acceptCode + "\n" 
    + "\n"

let getKey (key:string) arr = 
    try 
       let item = (Array.find (fun (s:String) -> s.StartsWith(key)) arr)
       item.Substring key.Length
    with
        | _ -> ""

let timer (ctrl : MailboxProcessor<Msg>) interval = async {
    while true do
        do! Async.Sleep interval
        ctrl.Post(Tick <| Time.New(DateTime.Now))            
    }
 
let runController (ct : CancellationToken) = 
    startMailboxProcessor ct (fun (inbox : MailboxProcessor<Msg>) ->
        let listeners = new ResizeArray<_>()
        async {
            while not ct.IsCancellationRequested do
                let! msg = inbox.Receive()
                match msg with
                | Connect l -> 
                    Console.WriteLine "Connect"
                    listeners.Add(l)
                | Disconnect l -> 
                    Console.WriteLine "Disconnect"
                    listeners.Remove(l) |> ignore
                | Tick msg -> listeners.ForEach(fun l -> l.Post msg)
        }
    )

let isWebSocketsUpgrade (lines: string array) = 
    [| "Upgrade: websocket" |] 
    |> Array.map(fun x->lines |> Array.exists(fun y->x.ToLower()=y.ToLower()))
    |> Array.reduce(fun x y->x && y)

let runWorker (tcp : TcpClient) (ctrl : MailboxProcessor<Msg>) ct = 
    ignore <| startMailboxProcessor ct (fun (inbox : MailboxProcessor<Time>) ->
        let rec handshake = async {
            let ns = tcp.GetStream()
            let sb = new StringBuilder()
            while ns.DataAvailable do
                let bytes = Array.create tcp.ReceiveBufferSize (byte 0)
                let bytesReadCount = ns.Read (bytes, 0, bytes.Length)
                sb.AppendFormat("{0}", Encoding.ASCII.GetString(bytes, 0, bytesReadCount))
            let bytes = Encoding.ASCII.GetBytes(sb.ToString())
            if tcp.ReceiveBufferSize > 0 then
                let lines = bytes |> System.Text.UTF8Encoding.UTF8.GetString |> fun hs->hs.Split([|"\n";"\r"|], StringSplitOptions.RemoveEmptyEntries)
                match isWebSocketsUpgrade lines with
                | true ->
                    let acceptStr = (getKey "Sec-WebSocket-Key:" lines).Substring(1) |> calcWSAccept6455 |> createAcceptString6455
                    do! ns.AsyncWrite <| Encoding.ASCII.GetBytes acceptStr
                    return! run ns
                | x ->
                    printfn "Error: Not a WebSocketUpgrade %A" x
                    tcp.Close()
            else
                printfn "ZERO %A" tcp.ReceiveBufferSize
                tcp.Close()
            }
        and run (ns : NetworkStream) = async {
            let json = System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof<Time>)
            ctrl.Post(Connect inbox)
            try
                while not ct.IsCancellationRequested do
                    let! time = inbox.Receive()
                    let ms = new MemoryStream()
                    json.WriteObject(ms, time)
                    do ns.WriteByte(byte 0x00)
                    do! ns.AsyncWrite(ms.ToArray())
                    do ns.WriteByte(byte 0xFF)
                    ms.Dispose()                                                      
            finally
                ns.Close()
                ctrl.Post(Disconnect inbox)
        }
        handshake
   )

 
let runRequestDispatcher () =
    let listener = new TcpListener(IPAddress.Parse(ipAddress), port)
    let cts = new CancellationTokenSource()
    let token = cts.Token
 
    let controller = runController token
    Async.Start (timer controller 1000, token)
 
    let main = async {
        try
            listener.Start(10)
            while not cts.IsCancellationRequested do
                let! client = Async.FromBeginEnd(listener.BeginAcceptTcpClient, listener.EndAcceptTcpClient)
                runWorker client controller token
        finally
            listener.Stop()       
    }
 
    Async.Start(main, token)
 
    { new IDisposable with member x.Dispose() = cts.Cancel()}
 
let dispose = runRequestDispatcher ()
printfn "press any key to stop..."
Console.ReadKey() |> ignore
dispose.Dispose()
