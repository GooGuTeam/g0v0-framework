// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;

namespace osu.Framework.IO.Stores
{
    public class DllResourceStore : IResourceStore<byte[]>
    {
        private readonly Assembly assembly;
        private readonly string prefix;

        public DllResourceStore(string dllName)
        {
            string filePath = Path.Combine(Path.GetDirectoryName(Assembly.GetCallingAssembly().Location).AsNonNull(), dllName);

            // prefer the local file if it exists, else load from assembly cache.
            assembly = File.Exists(filePath) ? Assembly.LoadFrom(filePath) : Assembly.Load(Path.GetFileNameWithoutExtension(dllName));

            prefix = Path.GetFileNameWithoutExtension(dllName);
        }

        public DllResourceStore(AssemblyName name)
            : this(Assembly.Load(name))
        {
        }

        public DllResourceStore(Assembly assembly)
        {
            this.assembly = assembly;
            prefix = assembly.GetName().Name;
        }

        public byte[] Get(string name)
        {
            this.LogIfNonBackgroundThread(name);

            using (Stream input = GetStream(name))
                return input?.ReadAllBytesToArray();
        }

        public virtual async Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            this.LogIfNonBackgroundThread(name);

            using (Stream input = GetStream(name))
            {
                if (input == null)
                    return null;

                return await input.ReadAllBytesToArrayAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Retrieve a list of available resources provided by this store.
        /// </summary>
        public IEnumerable<string> GetAvailableResources() =>
            assembly.GetManifestResourceNames().Select(n =>
            {
                n = n.Substring(n.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length + 1 : 0);

                int lastDot = n.LastIndexOf('.');

                char[] chars = n.ToCharArray();

                for (int i = 0; i < lastDot; i++)
                {
                    if (chars[i] == '.')
                        chars[i] = '/';
                }

                return new string(chars);
            });

        public Stream GetStream(string name)
        {
            this.LogIfNonBackgroundThread(name);

            string[] split = name.Split('/');
            for (int i = 0; i < split.Length - 1; i++)
                split[i] = ManifestFolderName(split[i]);

            return assembly?.GetManifestResourceStream($@"{prefix}.{string.Join('.', split)}");
        }

        /// <summary>
        /// The form MSBuild gives a folder name in manifest resource names (<c>CreateManifestResourceName.MakeValidEverettFolderIdentifier</c>):
        /// each <c>.</c>-separated part becomes an identifier, where invalid characters become <c>_</c> and a part not starting with a letter
        /// or <c>_</c> gets a leading <c>_</c> (<c>Long Notes</c> → <c>Long_Notes</c>, <c>4k</c> → <c>_4k</c>, <c>my-dir</c> → <c>my_dir</c>, <c>v1.2</c> → <c>v1._2</c>).
        /// </summary>
        internal static string ManifestFolderName(string folder)
        {
            var result = new StringBuilder(folder.Length + 1);
            string[] parts = folder.Split('.');

            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    result.Append('.');

                string part = parts[i];
                if (part.Length == 0)
                    continue;

                if (isValidIdentifierStart(part[0]))
                    result.Append(part[0]);
                else
                {
                    result.Append('_');
                    if (isValidIdentifierPart(part[0]))
                        result.Append(part[0]);
                }

                for (int j = 1; j < part.Length; j++)
                    result.Append(isValidIdentifierPart(part[j]) ? part[j] : '_');
            }

            // A folder cannot be a single underscore.
            if (result.Length == 1 && result[0] == '_')
                result.Append('_');

            return result.ToString();
        }

        private static bool isValidIdentifierStart(char c) => char.IsLetter(c) || char.GetUnicodeCategory(c) == UnicodeCategory.ConnectorPunctuation;

        private static bool isValidIdentifierPart(char c) => char.IsLetterOrDigit(c)
                                                             || char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark
                                                                 or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

        #region IDisposable Support

        public void Dispose()
        {
        }

        #endregion
    }
}
