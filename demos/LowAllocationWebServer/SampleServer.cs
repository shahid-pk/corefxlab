// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Net.Http;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Formatting;
using System.Text.Http;
using System.Text.Json;
using System.Text.Parsing;
using System.Text.Utf8;

class SampleRestServer : HttpServer
{
    public readonly ApiRoutingTable<Api> Apis = new ApiRoutingTable<Api>();

    public enum Api
    {
        HelloWorld = 0,
        GetTime = 1,
        PostJson = 3,
    }

    public SampleRestServer(Log log, ushort port, byte address1, byte address2, byte address3, byte address4) : base(log, port, address1, address2, address3, address4)
    {
        Apis.Add(Api.HelloWorld, HttpMethod.Get, requestUri: "/plaintext");
        Apis.Add(Api.GetTime, HttpMethod.Get, requestUri: "/time");
        Apis.Add(Api.PostJson, HttpMethod.Post, requestUri: "/json"); // post body along the lines of: "{ "Count" = "3" }" 
    }

    protected override void WriteResponse(BufferFormatter formatter, HttpRequest request)
    {
        var api = Apis.Map(request.RequestLine);
        switch (api) {
            case Api.HelloWorld:
                WriteResponseForHelloWorld(formatter);
                break;
            case Api.GetTime:
                WriteResponseForGetTime(formatter, request.RequestLine);
                break;
            case Api.PostJson:
                WriteResponseForPostJson(formatter, request.RequestLine, request.Body);
                break;
            default:
                // TODO: this should be built into the base class
                WriteResponseFor404(formatter, request.RequestLine);
                break;
        }
    }

    // This method is a bit of a mess. We need to fix many Http and Json APIs
    void WriteResponseForPostJson(BufferFormatter formatter, HttpRequestLine requestLine, ReadOnlySpan<byte> body)
    {

        var bodyText = new Utf8String(body);
        Console.WriteLine(bodyText);

        //uint requestedCount = ReadCountUsingReader(bodyText).GetValueOrDefault(1);
        uint requestedCount = ReadCountUsingNonAllocatingDom(bodyText).GetValueOrDefault(1);

        // this needs to be fixed. It allocated unnecesarily
        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        var stream = new MemoryStream(buffer);
        var json = new JsonWriter(stream, FormattingData.Encoding.Utf8, prettyPrint: true);
        json.WriteObjectStart();
        json.WriteArrayStart();
        for (int i = 0; i < requestedCount; i++) {
            json.WriteString(DateTime.UtcNow.ToString()); // this needs to not allocate. There needs to be overload to WriteString that takes T:IBufferFormattable
        }
        json.WriteArrayEnd(); ;
        json.WriteObjectEnd();
        var responseBodyText = new Utf8String(buffer, 0, (int)stream.Position);

        formatter.WriteHttpStatusLine(new Utf8String("1.1"), new Utf8String("200"), new Utf8String("OK"));
        formatter.WriteHttpHeader(new Utf8String("Content-Length"), new Utf8String(responseBodyText.Length.ToString())); // all these allocations (sic!)
        formatter.WriteHttpHeader(new Utf8String("Content-Type"), new Utf8String("text/plain; charset=UTF-8"));
        formatter.WriteHttpHeader(new Utf8String("Server"), new Utf8String(".NET Core Sample Serve"));
        // TODO: this needs to not allocate
        formatter.WriteHttpHeader(new Utf8String("Date"), new Utf8String(DateTime.UtcNow.ToString("R"))); // this bad
        formatter.EndHttpHeaderSection();
        formatter.WriteHttpBody(responseBodyText);

        ArrayPool<byte>.Shared.Return(buffer);
    }

    static void WriteResponseForHelloWorld(BufferFormatter formatter)
    {
        formatter.WriteHttpStatusLine(new Utf8String("1.1"), new Utf8String("200"), new Utf8String("Ok"));
        formatter.WriteHttpHeader(new Utf8String("Content-Length"), new Utf8String("12"));
        formatter.WriteHttpHeader(new Utf8String("Content-Type"), new Utf8String("text/plain; charset=UTF-8"));
        formatter.WriteHttpHeader(new Utf8String("Server"), new Utf8String(".NET Core Sample Serve"));
        // TODO: this needs to not allocate
        formatter.WriteHttpHeader(new Utf8String("Date"), new Utf8String(DateTime.UtcNow.ToString("R")));
        formatter.EndHttpHeaderSection();
        formatter.WriteHttpBody(new Utf8String("Hello, World"));
    }

    static void WriteResponseForGetTime(BufferFormatter formatter, HttpRequestLine request)
    {
        // TODO: this needs to not allocate
        var body = string.Format(@"<html><head><title>Time</title></head><body>{0}</body></html>", DateTime.UtcNow.ToString("O"));
        WriteCommonHeaders(formatter, "1.1", "200", "Ok", keepAlive: false);
        // TOOD: this needs to not allocate
        formatter.WriteHttpHeader(new Utf8String("Content-Length"), new Utf8String(body.Length.ToString()));
        formatter.EndHttpHeaderSection();
        formatter.WriteHttpBody(new Utf8String(body));
    }

    uint? ReadCountUsingReader(Utf8String json)
    {
        uint count;
        var reader = new JsonReader(json);
        while (reader.Read()) {
            switch (reader.TokenType) {
                case JsonReader.JsonTokenType.Property:
                    var name = reader.GetName();
                    var value = reader.GetValue();
                    Console.WriteLine("Property {0} = {1}", name, value);
                    if (name == "Count") {
                        if (!InvariantParser.TryParse(value, out count)) {
                            return null;
                        }
                        return count;
                    }
                    break;
            }
        }
        return null;
    }

    uint? ReadCountUsingNonAllocatingDom(Utf8String json)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        json.CopyTo(buffer); // TODO: we need to eliminate the copy
        var parser = new JsonParser(buffer, json.Length);
        var o = parser.Parse();
        uint count = (uint)o["Count"];
        ArrayPool<byte>.Shared.Return(buffer);
        return count;
    }
}

