﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using ApprovalMax.Logging;
using Microsoft.Extensions.Logging;
using Xero.Api.Infrastructure.Interfaces;
using Xero.Api.Infrastructure.RateLimiter;

namespace Xero.Api.Infrastructure.Http
{
    // This has enough functionality to get the call the API.
    // Content and accept types are defaulted and it is always ask for the response to be compressed.
    // Json for GET and XML for PUT and POST
    // It uses IAuthenticator or ICertificateAuthenticator to do the signing
    internal class HttpClient
    {
        static ILogger Logger { get; } = LogManager.CreateLogger(nameof(HttpClient));
        static readonly int defaultTimeout = (int)TimeSpan.FromMinutes(5.5).TotalMilliseconds;

        private readonly string _baseUri;
        private readonly IAuthenticator _auth;
        private readonly IRateLimiter _rateLimiter;

        private readonly Dictionary<string, string> _headers;

        public DateTime? ModifiedSince { get; set; }
        public IUser User { get; set; }

        private IConsumer Consumer { get; set; }

        public event EventHandler<ApiCallEventArgs> ApiCalled;

        public HttpClient(string baseUri)
        {
            _baseUri = baseUri;
            _headers = new Dictionary<string, string>();
        }

        public HttpClient(string baseUri, IConsumer consumer, IUser user) : this(baseUri)
        {
            User = user;
            Consumer = consumer;
        }

        public HttpClient(string baseUri, IAuthenticator auth, IConsumer consumer, IUser user)
            : this(baseUri, consumer, user)
        {
            _auth = auth;
        }

        public HttpClient(string baseUri, IAuthenticator auth, IConsumer consumer, IUser user, IRateLimiter rateLimiter)
            : this(baseUri, auth, consumer, user)
        {
            _rateLimiter = rateLimiter;
        }

        public string UserAgent
        {
            get; set;
        }

        public Response Post(string endpoint, string data, string contentType = "application/xml", string query = null)
        {
            return Post(endpoint, Encoding.UTF8.GetBytes(data), contentType, query);
        }

        public Response Post(string endpoint, byte[] data, string contentType = "application/xml", string query = null)
        {
            try
            {
                return WriteToServer(endpoint, data, "POST", contentType, query);
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    var response = new Response((HttpWebResponse)we.Response);
                    we.Response.Close();
                    return response;
                }

                throw;
            }
        }

        public Response PostMultipartForm(string endpoint, string contentType, string name, string filename, byte[] payload)
        {
            return WriteToServerWithMultipart(endpoint, contentType, name, filename, payload);
        }

        public Response Put(string endpoint, string data, string contentType = "application/xml", string query = null)
        {
            try
            {
                return WriteToServer(endpoint, Encoding.UTF8.GetBytes(data), "PUT", contentType, query);
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    var response = new Response((HttpWebResponse)we.Response);
                    we.Response.Close();
                    return response;
                }

                throw;
            }
        }

        public Response Get(string endpoint, string query)
        {
            return MakeCall(endpoint, () => CreateRequest(endpoint, "GET", query: query));
        }

        public Response GetRaw(string endpoint, string mimeType, string query = null)
        {
            return MakeCall(endpoint, () => CreateRequest(endpoint, "GET", mimeType, query));
        }

        public Response Delete(string endpoint)
        {
            return MakeCall(endpoint, () => CreateRequest(endpoint, "DELETE"));
        }

        private HttpWebRequest CreateRequest(string endPoint, string method, string accept = "application/json", string query = null)
        {
            var uri = new UriBuilder(_baseUri)
            {
                Path = endPoint,
            };

            if (!string.IsNullOrWhiteSpace(query))
            {
                uri.Query = query;
            }

            var request = (HttpWebRequest)WebRequest.Create(uri.Uri);
            // TODO: params to control connections
            // request.ServicePoint.MaxIdleTime = 30000;
            // request.KeepAlive = false;

            request.Timeout = defaultTimeout;

            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            request.Accept = accept;
            request.Method = method;

            if (ModifiedSince.HasValue)
            {
                request.IfModifiedSince = ModifiedSince.Value;
            }

            if (_auth != null)
            {
                var oauthSignature = _auth.GetSignature(Consumer, User, request.RequestUri, method, Consumer);

                AddHeader("Authorization", oauthSignature);
            }

            AddHeaders(request);

            request.UserAgent = !string.IsNullOrWhiteSpace(UserAgent) ? UserAgent : "Xero Api wrapper - " + Consumer.ConsumerKey;

            _rateLimiter?.WaitUntilLimit();

            return request;
        }

        private void AddHeaders(WebRequest request)
        {
            foreach (var pair in _headers)
            {
                request.Headers.Add(pair.Key, pair.Value);
            }
        }

        public void AddHeader(string name, string value)
        {
            _headers[name] = value;
        }

        private static void WriteData(byte[] bytes, WebRequest request, string contentType)
        {
            request.ContentLength = bytes.Length;
            request.ContentType = contentType;

            using (var dataStream = request.GetRequestStream())
            {
                dataStream.Write(bytes, 0, bytes.Length);
            }
        }

        private Response WriteToServerWithMultipart(string endpoint, string contentType, string name, string filename, byte[] payload)
        {
            return MakeCall(endpoint, () =>
            {
                var request = CreateRequest(endpoint, "POST");
                WriteMultipartData(payload, request, contentType, name, filename);
                return request;
            });
        }

        private void WriteMultipartData(byte[] bytes, HttpWebRequest request, string contentType, string name, string filename)
        {
            var boundary = Guid.NewGuid();

            byte[] header = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\nContent-Disposition: form-data; name=" + name + "; FileName=" + filename + " \r\nContent-Type: " + contentType + "\r\n\r\n");
            byte[] trailer = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.KeepAlive = false;

            var contentLength = bytes.Length + header.Length + trailer.Length;

            request.ContentLength = contentLength;

            var dataStream = request.GetRequestStream();
            dataStream.Write(header, 0, header.Length);
            dataStream.Write(bytes, 0, bytes.Length);
            dataStream.Write(trailer, 0, trailer.Length);
            dataStream.Close();
        }

        private Response WriteToServer(string endpoint, byte[] data, string method, string contentType = "application/xml", string query = null)
        {
            return MakeCall(endpoint, () =>
            {
                var request = CreateRequest(endpoint, method, query: query);
                WriteData(data, request, contentType);
                return request;
            });
        }

        private Response MakeCall(string endpoint, Func<HttpWebRequest> createHttpWebRequest)
        {
            var method = "Unknown";
            string responseCode;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var request = createHttpWebRequest();
                method = request.Method;
                Logger.LogDebug("Sending data to Xero. {method}, {requestUri}", method, request.RequestUri);

                Response response;
                using (var webResponse = (HttpWebResponse)request.GetResponse())
                {
                    responseCode = webResponse.StatusCode.ToString();
                    stopwatch.Stop();
                    ApiCalled?.Invoke(this, new ApiCallEventArgs(endpoint, method, stopwatch.ElapsedMilliseconds, responseCode, null));
                    response = new Response(webResponse);
                }

                return response;
            }
            catch (WebException we)
            {
                Logger.LogWarning(we, "WebException occured during calling Xero API.");
                if (we.Response != null)
                {
                    var response = new Response((HttpWebResponse)we.Response);
                    we.Response.Close();
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Body.Contains("oauth_problem"))
                    {
                        responseCode = "RateExceeded";
                    }
                    else
                    {
                        responseCode = response.StatusCode.ToString();
                    }

                    stopwatch.Stop();
                    ApiCalled?.Invoke(this, new ApiCallEventArgs(endpoint, method, stopwatch.ElapsedMilliseconds, responseCode, response.Body));
                    return response;
                }

                throw;
            }
            finally
            {
                stopwatch.Stop();
            }
        }
    }
}
