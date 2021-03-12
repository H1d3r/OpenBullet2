﻿using RuriLib.Http.Helpers;
using RuriLib.Http.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Buffers;

namespace RuriLib.Http
{
    internal class HttpResponseBuilder
    {

        
        private PipeReader reader;
        private const string newLine = "\r\n";
        private readonly byte[] CRLF = Encoding.UTF8.GetBytes(newLine);
        private static byte[] CRLFCRLF_Bytes = { 13, 10, 13, 10 };
        private HttpResponse response;
        private NetworkStream networkStream;
        private Stream commonStream;
        private Dictionary<string, List<string>> contentHeaders;
        private int contentLength = -1;

        internal TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(10);

        internal HttpResponseBuilder()
        {
            //  pipe = new Pipe();

        }

        /// <summary>
        /// Builds an HttpResponse by reading a network stream.
        /// </summary>
        async internal Task<HttpResponse> GetResponseAsync(HttpRequest request, Stream stream,
            CancellationToken cancellationToken = default)
        {
            networkStream = stream as NetworkStream;
            commonStream = stream;
            reader = PipeReader.Create(stream);


            response = new HttpResponse
            {
                Request = request
            };

            contentHeaders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            await ReceiveFirstLineAsync(cancellationToken).ConfigureAwait(false);
            await ReceiveHeadersAsync(cancellationToken).ConfigureAwait(false);
            await ReceiveContentAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }

        // Parses the first line, for example
        // HTTP/1.1 200 OK
        private async Task ReceiveFirstLineAsync(CancellationToken cancellationToken = default)
        {
            var startingLine = string.Empty;

            // Read the first line from the Network Stream
            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buff = res.Buffer;
                int crlfIndex = buff.FirstSpan.IndexOf(CRLF);
                if (crlfIndex > -1)
                {
                    try
                    {
                        startingLine = Encoding.UTF8.GetString(res.Buffer.FirstSpan.Slice(0, crlfIndex));
                        var fields = startingLine.Split(' ');
                        response.Version = Version.Parse(fields[0].Trim()[5..]);
                        response.StatusCode = (HttpStatusCode)Enum.Parse(typeof(HttpStatusCode), fields[1]);
                        buff = buff.Slice(0, crlfIndex + 2); // add 2 bytes for the CRLF
                        reader.AdvanceTo(buff.End); // advance to the consumed position
                        break;
                    }
                    catch
                    {
                        throw new FormatException($"Invalid first line of the HTTP response: {startingLine}");
                    }
                }
                else
                {
                    // the responce is incomplete ex. (HTTP/1.1 200 O)
                    reader.AdvanceTo(buff.Start, buff.End); // nothing consumed but all the buffer examined loop and read more.
                }
            }



        }

        // Parses the headers
        private async Task ReceiveHeadersAsync(CancellationToken cancellationToken = default)
        {

            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buff = res.Buffer;
                if (buff.IsSingleSegment)
                {
                    if (ReadHeadersFastPath(ref buff))
                    {
                        reader.AdvanceTo(buff.Start, buff.End);
                        break;
                    }

                }
                else
                {
                    if (ReadHeadersSlowerPath(ref buff))
                    {
                        reader.AdvanceTo(buff.Start, buff.End);
                        break;
                    }
                }
            }

        }


        /// <summary>
        /// Reads all Header Lines using Span<T> For High Perfromace Parsing.
        /// </summary>
        /// <param name="buff">Buffered Data From Pipe</param>
        private bool ReadHeadersFastPath(ref ReadOnlySequence<byte> buff)
        {
            int endofheadersindex;
            if ((endofheadersindex = buff.FirstSpan.IndexOf(CRLFCRLF_Bytes)) > -1)
            {
                var spanLines = buff.FirstSpan.Slice(0, endofheadersindex + 4);
                var Lines = spanLines.SplitLines();// we use spanHelper class here to make a for each loop.
                foreach (var Line in Lines)
                {
                    string HeaderLine = Encoding.UTF8.GetString(Line);
                    ProcessHeaderLine(HeaderLine);
                }

                buff = buff.Slice(endofheadersindex + 4); // add 4 bytes for \r\n\r\n and to advance the pipe back in the calling method
                return true;
            }
            return false;
        }
        /// <summary>
        /// Reads all Header Lines using SequenceReader.
        /// </summary>
        /// <param name="buff">Buffered Data From Pipe</param>
        private bool ReadHeadersSlowerPath(ref ReadOnlySequence<byte> buff)
        {
            var reader = new SequenceReader<byte>(buff);

            while (reader.TryReadTo(out ReadOnlySpan<byte> Line, CRLF, true))
            {
                if (Line.Length == 0)
                {
                    break;
                }
                ProcessHeaderLine(Encoding.UTF8.GetString(Line));
            }
            if (!reader.Position.Equals(buff.Start)) // means we have read the headeers
            {
                buff = buff.Slice(reader.Position); // so we can advance the pipe.
                return true;
            }
            else
            {
                return false;
            }

        }

        private void ProcessHeaderLine(string header)
        {
            if (String.IsNullOrEmpty(header))
            {
                return;
            }
            var separatorPos = header.IndexOf(':');

            var headerName = header.Substring(0, separatorPos);
            var headerValue = header[(separatorPos + 1)..].Trim(' ', '\t', '\r', '\n');

            // If the header is Set-Cookie, add the cookie
            if (headerName.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                headerName.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase))
            {
                SetCookie(response, headerValue);
            }
            // If it's a content header
            else if (ContentHelper.IsContentHeader(headerName))
            {
                if (contentHeaders.TryGetValue(headerName, out var values))
                {
                    values.Add(headerValue);
                }
                else
                {
                    values = new List<string>
                        {
                            headerValue
                        };

                    contentHeaders.Add(headerName, values);
                }
            }
            else
            {
                response.Headers[headerName] = headerValue;
            }
        }

        // Sets the value of a cookie
        private static void SetCookie(HttpResponse response, string value)
        {
            if (value.Length == 0)
            {
                return;
            }

            var endCookiePos = value.IndexOf(';');
            var separatorPos = value.IndexOf('=');

            if (separatorPos == -1)
            {
                throw new FormatException($"Invalid cookie format: {value}");
            }

            string cookieValue;
            var cookieName = value.Substring(0, separatorPos);

            if (endCookiePos == -1)
            {
                cookieValue = value[(separatorPos + 1)..];
            }
            else
            {
                cookieValue = value.Substring(separatorPos + 1, (endCookiePos - separatorPos) - 1);
            }

            response.Request.Cookies[cookieName] = cookieValue;
        }

        // TODO: Make this async (need to refactor the mess below)
        private async Task ReceiveContentAsync(CancellationToken cancellationToken = default)
        {
            // If there are content headers
            if (contentHeaders.Count != 0)
            {
                contentLength = GetContentLength();

                // Try to get the body and write it to a MemoryStream
                var finaleResponceStream = await GetMessageBodySource(cancellationToken);
                // Rewind the stream and set the content of the response and its headers
                finaleResponceStream.Seek(0, SeekOrigin.Begin);
                response.Content = new StreamContent(finaleResponceStream);
                foreach (var pair in contentHeaders)
                {
                    response.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            }
        }

        private async Task<Stream> GetMessageBodySource(CancellationToken cancellationToken)
        {
            if (response.Headers.ContainsKey("Transfer-Encoding"))
            {
                if (contentHeaders.ContainsKey("Content-Encoding"))
                {
                    using (var compressedStream = GetZipStream(await ReceiveMessageBodyChunked(cancellationToken)))
                    {
                        var decompressedStream = new MemoryStream();
                        compressedStream.CopyTo(decompressedStream);
                        return decompressedStream;
                    }
                }
                else
                {
                    return await ReceiveMessageBodyChunked(cancellationToken);
                }
            }
            else //if (contentLength > -1)
            {
                if (contentHeaders.ContainsKey("Content-Encoding"))
                {
                    using (var compressedStream = GetZipStream(await ReciveContentLength(cancellationToken)))
                    {
                        var decompressedStream = new MemoryStream();
                        compressedStream.CopyTo(decompressedStream);
                        return decompressedStream;
                    }
                }
                else
                {
                    return await ReciveContentLength(cancellationToken);

                }
            }

        }


        private async Task<Stream> ReciveContentLength(CancellationToken cancellationToken)
        {
            MemoryStream contentlenghtStream = new MemoryStream(contentLength);

            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buff = res.Buffer;
                if (buff.IsSingleSegment)
                {
                    contentlenghtStream.Write(buff.FirstSpan);
                }
                else
                {
                    foreach (var seg in buff)
                    {
                        contentlenghtStream.Write(seg.Span);
                    }
                }
                reader.AdvanceTo(buff.End);
                if (contentlenghtStream.Length >= contentLength)
                {
                    return contentlenghtStream;
                }
            }
        }




        private int GetContentLength()
        {
            if (contentHeaders.TryGetValue("Content-Length", out var values))
            {
                if (int.TryParse(values[0], out var length))
                {
                    return length;
                }
            }

            return -1;
        }

        private string GetContentEncoding()
        {
            var encoding = "";

            if (contentHeaders.TryGetValue("Content-Encoding", out var values))
            {
                encoding = values[0];
            }

            return encoding;
        }



        // Загрузка тела сообщения частями.
        private async Task<Stream> ReceiveMessageBodyChunked(CancellationToken cancellationToken)
        {
            var chunkedDecoder = new ChunkedDecoderOptimized();
            while (true)
            {
                var res = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buff = res.Buffer;
                chunkedDecoder.Decode(ref buff);
                reader.AdvanceTo(buff.Start, buff.End);
                if (chunkedDecoder.Finished)
                {
                    return chunkedDecoder.DecodedStream;
                }

            }
        }





        private Stream GetZipStream(Stream stream)
        {
            var contentEncoding = GetContentEncoding().ToLower();
            stream.Seek(0, SeekOrigin.Begin);
            return contentEncoding switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress, false),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress, false),
                "br" => new BrotliStream(stream, CompressionMode.Decompress, false),
                _ => throw new InvalidOperationException($"'{contentEncoding}' not supported encoding format"),
            };
        }




    }
}