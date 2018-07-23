﻿using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.Rtsp.Authentication;
using RtspClientSharp.Utils;

namespace RtspClientSharp.Rtsp
{
    class RtspHttpTransportClient : RtspTransportClient
    {
        private TcpClient _streamDataClient;
        private TcpClient _commandsClient;
        private string _sessionCookie;
        private Authenticator _authenticator;
        private Stream _dataNetworkStream;
        private uint _commandCounter;

        public RtspHttpTransportClient(ConnectionParameters connectionParameters)
            : base(connectionParameters)
        {
        }

        public override async Task ConnectAsync(CancellationToken token)
        {
            _commandCounter = 0;
            _sessionCookie = Guid.NewGuid().ToString("N").Substring(0, 10);

            _streamDataClient = TcpClientFactory.Create();

            Uri connectionUri = ConnectionParameters.ConnectionUri;

            int httpPort = connectionUri.Port != -1 ? connectionUri.Port : Constants.DefaultHttpPort;

            await _streamDataClient.ConnectAsync(connectionUri.Host, httpPort);

            _dataNetworkStream = _streamDataClient.GetStream();

            string request = ComposeGetRequest();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);

            await _dataNetworkStream.WriteAsync(requestBytes, 0, requestBytes.Length, token);

            var buffer = new byte[Constants.MaxResponseHeadersSize];
            int read = await ReadUntilEndOfHeadersAsync(_dataNetworkStream, buffer);

            var ms = new MemoryStream(buffer, 0, read);
            var streamReader = new StreamReader(ms, Encoding.ASCII);

            string responseLine = streamReader.ReadLine();

            if (responseLine == null)
                throw new HttpBadResponseException("Empty response");

            string[] tokens = responseLine.Split(' ');

            if (tokens.Length != 3)
                throw new HttpRequestException("Invalid first response line");

            HttpStatusCode statusCode = (HttpStatusCode) int.Parse(tokens[1]);

            if (statusCode == HttpStatusCode.OK)
                return;

            if (statusCode == HttpStatusCode.Unauthorized &&
                !ConnectionParameters.Credentials.IsEmpty() &&
                _authenticator == null)
            {
                NameValueCollection headers = HeadersParser.ParseHeaders(streamReader);

                string authenticateHeader = headers.Get(WellKnownHeaders.WwwAuthenticate);

                if (string.IsNullOrEmpty(authenticateHeader))
                    throw new HttpBadResponseCodeException(statusCode);

                _authenticator = Authenticator.Create(ConnectionParameters.Credentials, authenticateHeader);

                _streamDataClient.Dispose();

                await ConnectAsync(token);
                return;
            }

            throw new HttpBadResponseCodeException(statusCode);
        }

        public override Stream GetStream()
        {
            if (_streamDataClient == null || !_streamDataClient.Connected)
                throw new InvalidOperationException("Client is not connected");

            return _dataNetworkStream;
        }

        public override void Dispose()
        {
            if (_streamDataClient != null)
            {
                _streamDataClient.Client.Close();
                _streamDataClient.Dispose();
                _streamDataClient = null;
            }

            if (_commandsClient != null)
            {
                _commandsClient.Client.Close();
                _commandsClient.Dispose();
                _commandsClient = null;
            }
        }

        protected override async Task WriteAsync(byte[] buffer, int offset, int count)
        {
            using (_commandsClient = TcpClientFactory.Create())
            {
                Uri connectionUri = ConnectionParameters.ConnectionUri;

                int httpPort = connectionUri.Port != -1 ? connectionUri.Port : Constants.DefaultHttpPort;

                await _commandsClient.ConnectAsync(connectionUri.Host, httpPort);

                NetworkStream commandsStream = _commandsClient.GetStream();

                string base64CodedCommandString = Convert.ToBase64String(buffer, offset, count);
                byte[] base64CommandBytes = Encoding.ASCII.GetBytes(base64CodedCommandString);

                string request = ComposePostRequest(base64CommandBytes);
                byte[] requestBytes = Encoding.ASCII.GetBytes(request);

                await commandsStream.WriteAsync(requestBytes, 0, requestBytes.Length);
                await commandsStream.WriteAsync(base64CommandBytes, 0, base64CommandBytes.Length);
            }
        }

        protected override Task<int> ReadAsync(byte[] buffer, int offset, int count)
        {
            Debug.Assert(_dataNetworkStream != null, "_dataNetworkStream != null");
            return _dataNetworkStream.ReadAsync(buffer, offset, count);
        }

        protected override Task ReadExactAsync(byte[] buffer, int offset, int count)
        {
            Debug.Assert(_dataNetworkStream != null, "_dataNetworkStream != null");
            return _dataNetworkStream.ReadExactAsync(buffer, offset, count);
        }

        private string ComposeGetRequest()
        {
            string authorizationHeader = GetAuthorizationHeader(++_commandCounter, "GET", Array.Empty<byte>());

            return $"GET {ConnectionParameters.ConnectionUri.PathAndQuery} HTTP/1.0\r\n" +
                   $"x-sessioncookie: {_sessionCookie}\r\n" +
                   $"{authorizationHeader}\r\n";
        }

        private string ComposePostRequest(byte[] commandBytes)
        {
            string authorizationHeader = GetAuthorizationHeader(++_commandCounter, "POST", commandBytes);

            return $"POST {ConnectionParameters.ConnectionUri.PathAndQuery} HTTP/1.0\r\n" +
                   $"x-sessioncookie: {_sessionCookie}\r\n" +
                   "Content-Type: application/x-rtsp-tunnelled\r\n" +
                   $"Content-Length: {commandBytes.Length}\r\n" +
                   $"{authorizationHeader}\r\n";
        }

        private string GetAuthorizationHeader(uint counter, string method, byte[] requestBytes)
        {
            string authorizationHeader;

            if (_authenticator != null)
            {
                string headerValue = _authenticator.GetResponse(counter,
                    ConnectionParameters.ConnectionUri.PathAndQuery,
                    method, requestBytes);

                authorizationHeader = $"Authorization: {headerValue}\r\n";
            }
            else
                authorizationHeader = string.Empty;

            return authorizationHeader;
        }

        private async Task<int> ReadUntilEndOfHeadersAsync(Stream stream, byte[] buffer)
        {
            int offset = 0;

            int endOfHeaders;
            int totalRead = 0;

            do
            {
                int count = buffer.Length - totalRead;

                if (count == 0)
                    throw new RtspBadResponseException($"Response is too large (> {buffer.Length / 1024} KB)");

                int read = await stream.ReadAsync(buffer, offset, count);

                if (read == 0)
                    throw new EndOfStreamException("End of http stream");

                offset += read;
                totalRead += read;

                endOfHeaders = ArrayUtils.IndexOfBytes(buffer, Constants.DoubleCrlfBytes, 0, totalRead);
            } while (endOfHeaders == -1);

            return totalRead;
        }
    }
}