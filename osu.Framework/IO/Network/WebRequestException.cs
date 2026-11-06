// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net;

namespace osu.Framework.IO.Network
{
    /// <summary>
    /// An exception thrown by <see cref="WebRequest"/> when a request fails for a reason which originates outside of the caller's code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only exception type <see cref="WebRequest"/> throws for request-level failures, which allows consumers (and hosts) to
    /// tell network and remote-server problems apart from errors caused by their own code - a failure to deserialise a response, for
    /// instance, still surfaces as whichever exception the deserialiser threw.
    /// </para>
    /// <para>
    /// Derives from <see cref="WebException"/> so that consumers which already handle that type keep working.
    /// </para>
    /// </remarks>
    public class WebRequestException : WebException
    {
        /// <summary>
        /// The url of the request which failed.
        /// </summary>
        public string Url { get; }

        /// <summary>
        /// The status code returned by the server, or <see langword="null"/> if no response was received
        /// (i.e. the failure happened on the way to or from the server).
        /// </summary>
        public HttpStatusCode? StatusCode { get; }

        /// <summary>
        /// Whether the request failed without a response being received from the server
        /// (name resolution failure, connection refused or reset, TLS failure, timeout, ...).
        /// </summary>
        /// <remarks>
        /// Requests cancelled by the consumer are not considered connectivity failures as they were abandoned deliberately.
        /// </remarks>
        public bool IsConnectivityFailure => StatusCode == null && Status != WebExceptionStatus.RequestCanceled;

        /// <summary>
        /// Whether the server responded, but indicated that it failed to handle the request (i.e. a 5xx status code).
        /// </summary>
        public bool IsServerError => StatusCode is { } statusCode && (int)statusCode >= 500;

        /// <summary>
        /// Creates a new <see cref="WebRequestException"/> for a request which failed without a response being received.
        /// </summary>
        /// <param name="url">The url of the request which failed.</param>
        /// <param name="status">The kind of failure which occurred.</param>
        /// <param name="message">The message describing the failure.</param>
        /// <param name="innerException">The underlying exception, if any.</param>
        public WebRequestException(string url, WebExceptionStatus status, string message, Exception? innerException = null)
            : base(message, innerException, status, null)
        {
            Url = url;
        }

        /// <summary>
        /// Creates a new <see cref="WebRequestException"/> for a request which was answered with an unsuccessful status code.
        /// </summary>
        /// <param name="url">The url of the request which failed.</param>
        /// <param name="statusCode">The status code returned by the server.</param>
        /// <param name="message">The message describing the failure.</param>
        /// <param name="innerException">The underlying exception, if any.</param>
        public WebRequestException(string url, HttpStatusCode statusCode, string message, Exception? innerException = null)
            : base(message, innerException, WebExceptionStatus.ProtocolError, null)
        {
            Url = url;
            StatusCode = statusCode;
        }
    }
}
