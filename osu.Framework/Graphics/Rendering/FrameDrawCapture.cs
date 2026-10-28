// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using osu.Framework.Logging;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    // Opt-in, single-frame framework submission trace. Create <output>.request after reaching the desired screen,
    // or set G0V0_DRAW_CAPTURE_FRAMES to a comma-separated list of frame indices to write <output>.<frame>.json for each.
    internal sealed class FrameDrawCapture
    {
        private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions { WriteIndented = true };

        private readonly string output;
        private readonly HashSet<ulong> frames;
        private string currentOutput = string.Empty;
        private readonly List<object> draws = new List<object>();
        private bool recording;
        private bool completed;
        private ulong frame;
        private Vector2 size;
        private string backend = string.Empty;

        internal string? FlushSource { get; set; }

        private FrameDrawCapture(string output, HashSet<ulong> frames)
        {
            this.output = output;
            this.frames = frames;
        }

        internal static FrameDrawCapture? Create()
        {
            string? output = Environment.GetEnvironmentVariable("G0V0_DRAW_CAPTURE");
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var frames = new HashSet<ulong>();

            foreach (string part in (Environment.GetEnvironmentVariable("G0V0_DRAW_CAPTURE_FRAMES") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(part.Trim(), out ulong index))
                    frames.Add(index);
            }

            return new FrameDrawCapture(output, frames);
        }

        internal void Begin(ulong frameIndex, Vector2 windowSize, string renderer)
        {
            if (frames.Count > 0)
            {
                if (!frames.Contains(frameIndex))
                    return;

                currentOutput = Path.ChangeExtension(output, $"{frameIndex}.json");
            }
            else
            {
                if (completed || !File.Exists(output + ".request"))
                    return;

                currentOutput = output;
            }

            frame = frameIndex;
            size = windowSize;
            backend = renderer;
            recording = true;
        }

        internal void Record(PrimitiveTopology topology, int start, int count, object? shader, object? texture, bool backbuffer, object? batch)
        {
            if (!recording)
                return;

            draws.Add(new
            {
                index = draws.Count + 1,
                topology = topology.ToString(),
                vertexStart = start,
                vertexCount = count,
                shader = shader?.ToString(),
                shaderId = shader == null ? 0 : RuntimeHelpers.GetHashCode(shader),
                textureId = texture == null ? 0 : RuntimeHelpers.GetHashCode(texture),
                texture = texture is INativeTexture native ? $"{native.Identifier} {native.Width}x{native.Height}" : null,
                backbuffer,
                batch = batch?.GetType().Name,
                flush = FlushSource ?? "DirectOrCapacity",
                source = describeSource()
            });
        }

        // The draw nodes on the stack at submission time, innermost first. For a flush this is the node that caused it,
        // not necessarily the node whose vertices are submitted.
        private static string describeSource()
        {
            var names = new List<string>();

            foreach (var f in new StackTrace().GetFrames())
            {
                var type = f.GetMethod()?.DeclaringType;

                while (type?.DeclaringType != null && type.Name.StartsWith('<'))
                    type = type.DeclaringType;

                if (type != null && (type.Name.Contains("DrawNode") || type.Name.Contains("Drawable")) && (names.Count == 0 || names[^1] != type.FullName))
                    names.Add(type.FullName ?? type.Name);

                if (names.Count >= 3)
                    break;
            }

            return string.Join(" < ", names);
        }

        internal void Finish()
        {
            if (!recording)
                return;

            recording = false;
            completed = true;
            try
            {
                File.WriteAllText(currentOutput, JsonSerializer.Serialize(new
                {
                    capturedAtUtc = DateTime.UtcNow,
                    frame,
                    backend,
                    width = size.X,
                    height = size.Y,
                    drawCount = draws.Count,
                    scope = "Framework DrawVertices submissions; not a GPU command capture or timing measurement.",
                    draws
                }, json_options));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not write draw capture.");
            }
            finally
            {
                draws.Clear();
            }
        }
    }
}
