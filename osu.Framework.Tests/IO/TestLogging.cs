// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.IO.Network;
using osu.Framework.Logging;
using osu.Framework.Testing;

namespace osu.Framework.Tests.IO
{
    [TestFixture]
    public partial class TestLogging
    {
        [Test]
        public void TestExceptionLogging()
        {
            TestException resolvedException = null;

            void logTest(LogEntry entry)
            {
                if (entry.Exception is TestException ex)
                {
                    Assert.IsNull(resolvedException, "exception was forwarded more than once");
                    resolvedException = ex;
                }
            }

            using (var storage = new TemporaryNativeStorage(nameof(TestExceptionLogging)))
            {
                Logger.Storage = storage;
                Logger.Enabled = true;

                Logger.NewEntry += logTest;
                Logger.Error(new TestException(), "message");
                Logger.NewEntry -= logTest;

                Assert.IsNotNull(resolvedException, "exception wasn't forwarded by logger");

                Logger.Enabled = false;
                Logger.Flush();
            }
        }

        [Test]
        public void TestUnhandledExceptionLogging()
        {
            TestException resolvedException = null;

            void logTest(LogEntry entry)
            {
                if (entry.Exception is TestException ex)
                {
                    Assert.IsNull(resolvedException, "exception was forwarded more than once");
                    resolvedException = ex;
                }
            }

            Logger.NewEntry += logTest;

            try
            {
                using (var host = new TestRunHeadlessGameHost())
                {
                    var game = new TestGame();
                    game.Schedule(() => throw new TestException());
                    host.Run(game);
                }
            }
            catch
            {
                // catch crashing exception
            }

            Assert.IsNotNull(resolvedException, "exception wasn't forwarded by logger");
            Logger.NewEntry -= logTest;
        }

        [Test]
        public void TestUnhandledIgnoredException()
        {
            Assert.DoesNotThrow(() => runWithIgnoreCount(2, 2));
        }

        [Test]
        public void TestUnhandledIgnoredOnceException()
        {
            Assert.Throws<TestException>(() => runWithIgnoreCount(1, 2));
        }

        /// <summary>
        /// Ignore unhandled exceptions for the provided count.
        /// </summary>
        /// <param name="ignoreCount">Number of exceptions to ignore.</param>
        /// <param name="fireCount">How many exceptions to fire.</param>
        private void runWithIgnoreCount(int ignoreCount, int fireCount)
        {
            using (var host = new TestRunHeadlessGameHost())
            {
                host.ExceptionThrown += _ => ignoreCount-- > 0;

                var game = new TestGame();

                for (int i = 0; i < fireCount; i++)
                    game.Schedule(() => throw new TestException());
                game.Schedule(game.Exit);

                host.Run(game);
            }
        }

        [Test]
        public void TestGameUpdateExceptionNoLogging()
        {
            Assert.Throws<TestException>(() =>
            {
                using (var host = new TestRunHeadlessGameHost())
                    host.Run(new CrashTestGame());
            });
        }

        private partial class CrashTestGame : Game
        {
            protected override void Update()
            {
                base.Update();
                throw new TestException();
            }
        }

        [Test]
        public void TestTaskExceptionLogging()
        {
            Exception resolvedException = null;

            void logTest(LogEntry entry)
            {
                if (entry.Exception is TestException ex)
                {
                    Assert.IsNull(resolvedException, "exception was forwarded more than once");
                    resolvedException = ex;
                }
            }

            Logger.NewEntry += logTest;

            using (new BackgroundGameHeadlessGameHost())
            {
                // see https://tpodolak.com/blog/2015/08/10/tpl-exception-handling-and-unobservedtaskexception-issue/
                // needs to be in a separate method so the Task gets GC'd.
                performTaskException();

                collectAndFireUnobserved();
            }

            Assert.IsNotNull(resolvedException, "exception wasn't forwarded by logger");
            Logger.NewEntry -= logTest;
        }

        private void performTaskException()
        {
            var task = Task.Run(() => throw new TestException());
            while (!task.IsCompleted)
                Thread.Sleep(1);
        }

        /// <summary>
        /// A request failing is an expected part of operating online rather than an application error,
        /// so it must be logged away from the pathway consumers use to notify the user and report to developers.
        /// </summary>
        [Test]
        public void TestUnobservedRequestFailureLogging()
        {
            LogEntry resolvedEntry = null;

            void logTest(LogEntry entry)
            {
                if (entry.Exception is WebRequestException)
                    resolvedEntry = entry;
            }

            Logger.NewEntry += logTest;

            try
            {
                using (new BackgroundGameHeadlessGameHost())
                {
                    performRequestFailure();
                    collectAndFireUnobserved();
                }
            }
            finally
            {
                Logger.NewEntry -= logTest;
            }

            Assert.IsNotNull(resolvedEntry, "request failure wasn't forwarded by logger");
            Assert.AreEqual(LogLevel.Verbose, resolvedEntry.Level, "network failures must not be logged at a level which triggers error reporting");
            Assert.AreEqual(LoggingTarget.Network, resolvedEntry.Target);
        }

        private void performRequestFailure()
        {
            var task = Task.Run(() => throw new WebRequestException("https://example.com/api", HttpStatusCode.NotFound, "request failed"));
            while (!task.IsCompleted)
                Thread.Sleep(1);
        }

        [Test]
        public void TestRecursiveExceptionLogging()
        {
            TestExceptionWithInnerException resolvedException = null;
            TestInnerException resolvedInnerException = null;

            void logTest(LogEntry entry)
            {
                if (entry.Exception is TestExceptionWithInnerException ex)
                {
                    Assert.IsNull(resolvedException, "exception was forwarded more than once");
                    resolvedException = ex;
                }

                if (entry.Exception is TestInnerException inner)
                {
                    Assert.IsNull(resolvedInnerException, "exception was forwarded more than once");
                    resolvedInnerException = inner;
                }
            }

            Logger.Enabled = true;
            Logger.NewEntry += logTest;
            Logger.Error(new TestExceptionWithInnerException(), "message", recursive: true);
            Logger.NewEntry -= logTest;

            Assert.IsNotNull(resolvedException, "exception wasn't forwarded by logger");
            Assert.IsNotNull(resolvedInnerException, "inner exception wasn't forwarded by logger");
        }

        private class TestException : Exception
        {
        }

        public class TestExceptionWithInnerException : Exception
        {
            public TestExceptionWithInnerException()
                : base("", new TestInnerException())
            {
            }
        }

        private class TestInnerException : Exception
        {
        }

        [TearDown]
        public void TearDown()
        {
            // Safety against any unobserved exceptions being left in the pipe.
            collectAndFireUnobserved();
        }

        /// <summary>
        /// Forcefully collect so the unobserved exception isn't handled by a future test execution.
        /// </summary>
        private static void collectAndFireUnobserved()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
