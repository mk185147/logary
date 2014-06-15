﻿module Logary.Riemann.Client

open System
open System.Net
open System.IO

open ProtoBuf

open Logary.Riemann.Messages

/// Converts int to networkByteOrder
let toBytes (len : int) =
  BitConverter.GetBytes(IPAddress.HostToNetworkOrder len)

/// Converts byte[] from networkByteOrder
let fromBytes (buf : byte []) =
  let i = BitConverter.ToInt32(buf, 0)
  IPAddress.NetworkToHostOrder i

/// Reads a Riemann-length from a stream at its current position
let readLen (stream : Stream) = async {
  let lenBuf = Array.zeroCreate 4 // TODO: use extracted buf
  let! wasRead = stream.AsyncRead(lenBuf, 0, 4) // TODO: faster with no async?
  if wasRead = 4 then
    return fromBytes lenBuf
  else
    return raise <| EndOfStreamException("unexpected EOF while reading len") }

let send (stream : Stream) (msg : byte array) = async {
  do! stream.AsyncWrite(toBytes msg.Length)
  do! stream.AsyncWrite(msg, 0, msg.Length)
  do stream.Flush() // TODO: blocking?
  }

let private transfer len (source : Stream) (target : Stream) =
  let bufSize = 0x2000
  let buf = Array.zeroCreate bufSize // TODO: extract
  let rec read' amountRead = async {
    if amountRead >= len then return ()
    else
      let toRead = Math.Min(bufSize, len - amountRead)
      let! wasRead = source.AsyncRead(buf, 0, toRead)
      if wasRead <> toRead then raise <| EndOfStreamException("unexpected EOF")
      else
        do! target.AsyncWrite(buf, 0, wasRead)
        return! read' (wasRead + amountRead) }
  read' 0

let sendMessage (stream : Stream) (msg : Msg) = async {
  use ms = new MemoryStream() // TODO: re-use MS?
  Serializer.Serialize(ms, msg)
  do! transfer (int ms.Position) ms stream }

let readMessage (stream : Stream) = async {
  let! toRead = readLen stream
  use ms = new MemoryStream() // TODO: re-use MS?
  do! transfer toRead stream ms
  ms.Seek(0L, SeekOrigin.Begin) |> ignore
  return Serializer.Deserialize<Msg> ms }

let sendEvents (stream : Stream) (es : Event seq) =
  Msg(false, "", [], Query(), es) |> sendMessage stream

let sendQuery (stream : Stream) (q : Query) =
  Msg(false,"", [], q, []) |> sendMessage stream
