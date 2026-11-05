// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net;
using NUnit.Framework;
using osu.Framework.IO.Network;

namespace osu.Framework.Tests.IO
{
    [TestFixture]
    public class TestWebRequestException
    {
        [Test]
        public void TestRemainsCatchableAsWebException()
        {
            var exception = new WebRequestException("https://example.com/api", HttpStatusCode.NotFound, "not found");

            Assert.Multiple(() =>
            {
                Assert.IsInstanceOf<WebException>(exception);
                Assert.AreEqual("https://example.com/api", exception.Url);
            });
        }

        [Test]
        public void TestClientError([Values(400, 401, 403, 404, 422, 429)] int statusCode)
        {
            var exception = new WebRequestException("https://example.com/api", (HttpStatusCode)statusCode, "client error");

            Assert.Multiple(() =>
            {
                Assert.AreEqual((HttpStatusCode)statusCode, exception.StatusCode);
                Assert.IsFalse(exception.IsServerError, "4xx responses are not server errors");
                Assert.IsFalse(exception.IsConnectivityFailure, "a received response proves connectivity");
                Assert.AreEqual(WebExceptionStatus.ProtocolError, exception.Status);
            });
        }

        [Test]
        public void TestServerError([Values(500, 502, 503, 504)] int statusCode)
        {
            var exception = new WebRequestException("https://example.com/api", (HttpStatusCode)statusCode, "server error");

            Assert.Multiple(() =>
            {
                Assert.IsTrue(exception.IsServerError);
                Assert.IsFalse(exception.IsConnectivityFailure);
            });
        }

        [Test]
        public void TestConnectivityFailure([Values(
                                                WebExceptionStatus.Timeout,
                                                WebExceptionStatus.ConnectFailure,
                                                WebExceptionStatus.NameResolutionFailure,
                                                WebExceptionStatus.ConnectionClosed,
                                                WebExceptionStatus.ReceiveFailure,
                                                WebExceptionStatus.SecureChannelFailure)]
                                            WebExceptionStatus status)
        {
            var exception = new WebRequestException("https://example.com/api", status, "failure");

            Assert.Multiple(() =>
            {
                Assert.IsTrue(exception.IsConnectivityFailure);
                Assert.IsFalse(exception.IsServerError);
                Assert.IsNull(exception.StatusCode);
                Assert.AreEqual(status, exception.Status);
            });
        }

        /// <summary>
        /// A request abandoned by its consumer is not a failure of the network, so it must not be treated as one.
        /// </summary>
        [Test]
        public void TestCancelledRequestIsNotConnectivityFailure()
        {
            var exception = new WebRequestException("https://example.com/api", WebExceptionStatus.RequestCanceled, "aborted by user");

            Assert.Multiple(() =>
            {
                Assert.IsFalse(exception.IsConnectivityFailure);
                Assert.IsFalse(exception.IsServerError);
            });
        }
    }
}
