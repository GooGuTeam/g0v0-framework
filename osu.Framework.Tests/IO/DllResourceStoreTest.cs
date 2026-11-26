// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.IO.Stores;
using osu.Framework.Tests.Visual;

namespace osu.Framework.Tests.IO
{
    public class DllResourceStoreTest
    {
        [Test]
        public async Task TestSuccessfulAsyncLookup()
        {
            var resourceStore = new DllResourceStore(typeof(FrameworkTestScene).Assembly);

            byte[]? stream = await resourceStore.GetAsync("Resources.Tracks.sample-track.mp3").ConfigureAwait(false);
            Assert.That(stream, Is.Not.Null);
        }

        [Test]
        public async Task TestFailedAsyncLookup()
        {
            var resourceStore = new DllResourceStore(typeof(FrameworkTestScene).Assembly);

            byte[]? stream = await resourceStore.GetAsync("Resources.Tracks.sample-track.mp5").ConfigureAwait(false);
            Assert.That(stream, Is.Null);
        }

        [TestCase("Samples", "Samples")]
        [TestCase("my-dir", "my_dir")]
        [TestCase("Long Notes", "Long_Notes")]
        [TestCase("4k", "_4k")]
        [TestCase("-x", "_x")]
        [TestCase("v1.2", "v1._2")]
        [TestCase("a..b", "a..b")]
        [TestCase("-", "__")]
        [TestCase("_", "__")]
        public void TestManifestFolderName(string folder, string expected)
            => Assert.That(DllResourceStore.ManifestFolderName(folder), Is.EqualTo(expected));
    }
}
