// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Development;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ExceptionExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Exceptions
{
    [TestFixture]
    public partial class TestLoadExceptions
    {
        [TearDown]
        public void ResetLoadErrorHandling()
        {
            LoadErrorHandling.Enabled = false;
            LoadErrorHandling.PlaceholderFactory = null;
        }

        [Test]
        public void TestLoadIntoInvalidTarget()
        {
            var loadable = new DelayedTestBoxAsync();
            var loadTarget = new LoadTarget(loadable);

            Assert.Throws<InvalidOperationException>(() => loadTarget.PerformAsyncLoad());
        }

        /// <summary>
        /// Test environments must never isolate load failures - a regression has to surface as a test failure instead.
        /// </summary>
        [Test]
        public void TestLoadErrorHandlingDisabledByDefault()
        {
            Assert.IsFalse(LoadErrorHandling.Enabled);
        }

        [Test]
        public void TestSingleSyncAdd()
        {
            var loadable = new DelayedTestBoxAsync();

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(loadable);
                    Assert.IsTrue(loadable.LoadState == LoadState.Ready);
                    Assert.AreEqual(loadable.Parent, g);
                    g.Exit();
                });
            });
        }

        [Test]
        public void TestUnobservedException()
        {
            Exception loggedException = null;

            Logger.NewEntry += newLogEntry;

            try
            {
                var exception = Assert.Throws<AggregateException>(() =>
                {
                    Task.Factory.StartNew(() =>
                    {
                        runGameWithLogic(g =>
                        {
                            g.Scheduler.Add(() => Task.Run(() => throw new InvalidOperationException()));
                            g.Scheduler.AddDelayed(collect, 1, true);

                            if (loggedException != null)
                                throw loggedException;
                        });
                    }, TaskCreationOptions.LongRunning).Wait(TimeSpan.FromSeconds(10));

                    Assert.Fail("Game execution was not aborted");
                });

                Assert.True(exception?.AsSingular() is InvalidOperationException);
            }
            finally
            {
                Logger.NewEntry -= newLogEntry;
            }

            void newLogEntry(LogEntry entry) => loggedException = entry.Exception;
        }

        private static void collect()
        {
            GC.Collect();
        }

        [Test]
        public void TestSingleAsyncAdd()
        {
            var loadable = new DelayedTestBoxAsync();
            var loadTarget = new LoadTarget(loadable);

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(loadTarget);
                    loadTarget.PerformAsyncLoad();
                }, _ => loadable.Parent == loadTarget);
            });
        }

        [Test]
        public void TestDoubleAsyncLoad()
        {
            var loadable = new DelayedTestBoxAsync();
            var loadTarget = new LoadTarget(loadable);

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(loadTarget);
                    loadTarget.PerformAsyncLoad();
                    loadTarget.PerformAsyncLoad(false);
                }, _ => loadable.Parent == loadTarget);
            });
        }

        [Test]
        public void TestDoubleAsyncAddFails()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                runGameWithLogic(g =>
                {
                    var loadable = new DelayedTestBoxAsync();
                    var loadTarget = new LoadTarget(loadable);

                    g.Add(loadTarget);

                    loadTarget.PerformAsyncLoad();
                    loadTarget.PerformAsyncLoad();
                });
            });
        }

        [Test]
        public void TestTargetDisposedDuringAsyncLoad()
        {
            Assert.Throws<ObjectDisposedException>(() =>
            {
                runGameWithLogic(g =>
                {
                    var loadable = new DelayedTestBoxAsync();
                    var loadTarget = new LoadTarget(loadable);

                    g.Add(loadTarget);

                    loadTarget.PerformAsyncLoad();

                    while (loadable.LoadState < LoadState.Loading)
                        Thread.Sleep(1);

                    g.Dispose();
                });
            });
        }

        [Test]
        public void TestLoadableDisposedDuringAsyncLoad()
        {
            Assert.Throws<ObjectDisposedException>(() =>
            {
                runGameWithLogic(g =>
                {
                    var loadable = new DelayedTestBoxAsync();
                    var loadTarget = new LoadTarget(loadable);

                    g.Add(loadTarget);

                    loadTarget.PerformAsyncLoad();

                    while (loadable.LoadState < LoadState.Loading)
                        Thread.Sleep(1);

                    loadable.Dispose();
                });
            });
        }

        /// <summary>
        /// The async load completion callback is scheduled on the <see cref="Game"/>. The callback is generally used to add the child to the container,
        /// however it is possible for the container to be disposed when this occurs due to being scheduled on the <see cref="Game"/>. If this occurs,
        /// the cancellation is invoked and the completion task should not be run.
        ///
        /// This is a very timing-dependent test which performs the following sequence:
        /// LoadAsync -> schedule Callback -> dispose parent -> invoke scheduled callback
        /// </summary>
        [Test]
        public void TestDisposeAfterLoad()
        {
            Assert.DoesNotThrow(() =>
            {
                var loadTarget = new LoadTarget(new DelayedTestBoxAsync());

                bool allowDispose = false;
                bool disposeTriggered = false;
                bool updatedAfterDispose = false;

                runGameWithLogic(g =>
                {
                    g.Add(loadTarget);
                    loadTarget.PerformAsyncLoad().ContinueWith(_ => allowDispose = true);
                }, g =>
                {
                    // The following code is done here for a very specific reason, but can occur naturally in normal use
                    // This delegate is essentially the first item in the game's scheduler, so it will always run PRIOR to the async callback

                    if (disposeTriggered)
                        updatedAfterDispose = true;

                    if (allowDispose)
                    {
                        // Async load has complete, the callback has been scheduled but NOT run yet
                        // Dispose the parent container - this is done by clearing the game
                        g.Clear(true);
                        disposeTriggered = true;
                    }

                    // After disposing the parent, one update loop is required
                    return updatedAfterDispose;
                });
            });
        }

        [Test]
        public void TestSyncLoadException()
        {
            Assert.Throws<AsyncTestException>(() => runGameWithLogic(g => g.Add(new DelayedTestBoxAsync(true))));
        }

        /// <summary>
        /// With <see cref="LoadErrorHandling.Enabled"/>, a component which throws while loading is discarded and replaced by a placeholder,
        /// while its siblings load unaffected.
        /// </summary>
        [Test]
        public void TestSyncLoadExceptionIsolated()
        {
            LoadErrorHandling.Enabled = true;

            var target = new IsolationTarget();

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(target);

                    // the sibling components must be unaffected by the failure.
                    Assert.AreEqual(LoadState.Ready, target.HealthyBefore.LoadState);
                    Assert.AreEqual(LoadState.Ready, target.HealthyAfter.LoadState);
                    Assert.AreSame(target, target.HealthyBefore.Parent);
                    Assert.AreSame(target, target.HealthyAfter.Parent);

                    // the failed component is discarded, and never reaches a usable state.
                    Assert.IsFalse(target.Children.Contains(target.Failing));
                    Assert.IsNull(target.Failing.Parent);
                    Assert.Less(target.Failing.LoadState, LoadState.Ready);

                    var placeholders = target.Children.OfType<LoadErrorPlaceholder>().ToArray();

                    Assert.AreEqual(1, placeholders.Length);
                    Assert.AreSame(target.Failing, placeholders[0].FailedDrawable);
                    Assert.IsInstanceOf<AsyncTestException>(placeholders[0].Failure);
                    Assert.AreEqual(LoadState.Ready, placeholders[0].LoadState);

                    // the placeholder takes over the slot the failed component occupied, rather than being appended after its siblings.
                    Assert.AreSame(placeholders[0], target.Children[1]);

                    g.Exit();
                });
            });
        }

        /// <summary>
        /// A component which throws while completing its load (see <see cref="Drawable.LoadComplete"/>) is discarded and replaced by a placeholder,
        /// exactly as if it had thrown while loading.
        /// </summary>
        /// <remarks>
        /// This phase is worth calling out separately because it does not run in the load pipeline: <see cref="Drawable.LoadComplete"/> is invoked
        /// from <see cref="Drawable.UpdateSubTree"/>, i.e. later and on the update thread (see <see cref="Drawable.IsLoaded"/>). A component which is
        /// misconfigured in a way that only surfaces there - for example one which sets a mutually exclusive <see cref="Drawable.RelativeSizeAxes"/>
        /// or <see cref="CompositeDrawable.AutoSizeAxes"/> pair when its first value arrives - therefore fails <i>outside</i> the code path which
        /// performs load isolation.
        /// </remarks>
        [Test]
        public void TestLoadCompleteExceptionIsolated()
        {
            LoadErrorHandling.Enabled = true;

            var target = new IsolationTarget(new FailsOnLoadComplete());

            LoadErrorPlaceholder placeholder = null;
            Drawable occupyingChild = null;
            LoadState[] siblingStates = null;

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g => g.Add(target), _ =>
                {
                    placeholder = target.Children.OfType<LoadErrorPlaceholder>().FirstOrDefault();

                    if (placeholder == null)
                        return false;

                    occupyingChild = target.Children[1];
                    siblingStates = new[] { target.HealthyBefore.LoadState, target.HealthyAfter.LoadState };
                    return true;
                });
            });

            Assert.IsNotNull(placeholder);
            Assert.AreSame(target.Failing, placeholder.FailedDrawable);
            Assert.IsInstanceOf<AsyncTestException>(placeholder.Failure);

            // the placeholder takes over the slot the failed component occupied, rather than being appended after its siblings.
            Assert.AreSame(placeholder, occupyingChild);

            // the failed component never claimed any extent, so there is no slot worth imposing: the placeholder is left to size itself (which, for
            // the default placeholder, means its own content) rather than being given a slot in which it could not be seen.
            Assert.AreEqual(Axes.Both, placeholder.AutoSizeAxes);

            // the siblings are unaffected by the failure.
            Assert.AreEqual(LoadState.Loaded, siblingStates[0]);
            Assert.AreEqual(LoadState.Loaded, siblingStates[1]);
        }

        /// <summary>
        /// A placeholder which fails while completing its own load must not be replaced by another placeholder, indefinitely.
        /// </summary>
        [Test]
        public void TestFailingLoadCompletePlaceholderDoesNotRecurse()
        {
            LoadErrorHandling.Enabled = true;

            int created = 0;

            LoadErrorHandling.PlaceholderFactory = (_, _) =>
            {
                created++;
                return new FailsOnLoadComplete();
            };

            var target = new IsolationTarget();

            int frames = 0;
            int childrenAfterwards = -1;

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g => g.Add(target), _ =>
                {
                    // allow a few frames for the placeholder to run (and fail) its own load-complete phase.
                    if (++frames <= 10)
                        return false;

                    childrenAfterwards = target.InternalChildren.Count;
                    return true;
                });
            });

            // the placeholder is created once. a failure within it is logged and the slot is given up on, rather than answered with another placeholder.
            Assert.AreEqual(1, created);
            Assert.AreEqual(2, childrenAfterwards);
        }

        /// <summary>
        /// A placeholder which itself fails to load must not recurse into creating further placeholders, or throw.
        /// </summary>
        [Test]
        public void TestFailingPlaceholderDoesNotThrow()
        {
            LoadErrorHandling.Enabled = true;
            LoadErrorHandling.PlaceholderFactory = (_, _) => throw new InvalidOperationException();

            var target = new IsolationTarget();

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(target);

                    // no placeholder could be created, but the failure is still isolated and the siblings are unaffected.
                    Assert.AreEqual(2, target.InternalChildren.Count);
                    Assert.AreEqual(LoadState.Ready, target.HealthyBefore.LoadState);
                    Assert.AreEqual(LoadState.Ready, target.HealthyAfter.LoadState);

                    g.Exit();
                });
            });
        }

        /// <summary>
        /// A component which throws while being loaded asynchronously is discarded and reported via <see cref="CompositeDrawable.ChildLoadFailed"/>,
        /// rather than being thrown into the game's update loop.
        /// </summary>
        [Test]
        public void TestAsyncLoadExceptionIsolated()
        {
            LoadErrorHandling.Enabled = true;

            var loadable = new DelayedTestBoxAsync(true);
            var loadTarget = new LoadTarget(loadable);

            Drawable failed = null;
            Exception failure = null;

            loadTarget.ChildLoadFailed += (d, e) =>
            {
                failed = d;
                failure = e;
            };

            Assert.DoesNotThrow(() =>
            {
                runGameWithLogic(g =>
                {
                    g.Add(loadTarget);
                    loadTarget.PerformAsyncLoad();
                }, _ => failed != null);
            });

            Assert.AreSame(loadable, failed);
            Assert.IsInstanceOf<AsyncTestException>(failure);

            // the failed component is never added to the hierarchy (the completion callback does not run for it).
            Assert.IsEmpty(loadTarget.Children);
            Assert.IsNull(loadable.Parent);
        }

        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        private void runGameWithLogic(Action<Game> logic, Func<Game, bool> exitCondition = null)
        {
            Storage storage = null;

            try
            {
                using (var host = new TestRunHeadlessGameHost($"{GetType().Name}-{Guid.NewGuid()}", new HostOptions()))
                {
                    using (var game = new TestGame())
                    {
                        game.Schedule(() =>
                        {
                            storage = host.Storage;
                            host.UpdateThread.Scheduler.AddDelayed(() =>
                            {
                                if (exitCondition?.Invoke(game) == true)
                                    host.Exit();
                            }, 0, true);

                            logic(game);
                        });

                        host.Run(game);
                    }
                }
            }
            finally
            {
                try
                {
                    storage?.DeleteDirectory(string.Empty);
                }
                catch
                {
                    // May fail due to the file handles still being open on Windows, but this isn't a big problem for us
                }
            }
        }

        private partial class LoadTarget : Container
        {
            private readonly Drawable loadable;

            public LoadTarget(Drawable loadable)
            {
                this.loadable = loadable;
            }

            public Task PerformAsyncLoad(bool withAdd = true) => LoadComponentAsync(loadable, _ =>
            {
                if (withAdd) Add(loadable);
            });
        }

        private partial class IsolationTarget : Container
        {
            public readonly Drawable HealthyBefore = new Box { Size = new Vector2(10) };
            public readonly Drawable Failing;
            public readonly Drawable HealthyAfter = new Box { Size = new Vector2(10) };

            public IsolationTarget(Drawable failing = null)
            {
                Failing = failing ?? new FailingBox();
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                InternalChildren = new[] { HealthyBefore, Failing, HealthyAfter };
            }
        }

        /// <summary>
        /// A component which always throws while loading.
        /// </summary>
        public partial class FailingBox : Box
        {
            [BackgroundDependencyLoader]
            private void load() => throw new AsyncTestException();
        }

        /// <summary>
        /// A component which always throws while completing its load, rather than while loading.
        /// </summary>
        public partial class FailsOnLoadComplete : CompositeDrawable
        {
            protected override void LoadComplete() => throw new AsyncTestException();
        }

        public partial class DelayedTestBoxAsync : Box
        {
            private readonly bool throws;

            public DelayedTestBoxAsync(bool throws = false)
            {
                this.throws = throws;
                Size = new Vector2(50);
                Colour = Color4.Green;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                Task.Delay((int)(1000 / Clock.Rate)).WaitSafely();
                if (throws)
                    throw new AsyncTestException();
            }
        }

        private class AsyncTestException : Exception
        {
        }
    }
}
