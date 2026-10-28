// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// A visual test scene, set up through its own steps, whose frames are timed.
    /// </summary>
    public sealed class BenchmarkCase
    {
        /// <summary>
        /// The name shown in results, unique within a matrix.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The <see cref="TestScene"/> to load.
        /// </summary>
        public Type SceneType { get; }

        /// <summary>
        /// What the case exercises.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// A test method of the scene whose steps build the content, as the test browser runs it: set-up steps, then the method's steps.
        /// Performance scenes build their content from their sliders' defaults and need none.
        /// </summary>
        public string? Test { get; init; }

        /// <summary>
        /// Arguments for <see cref="Test"/>, as given by its test cases.
        /// </summary>
        public object[]? TestArguments { get; init; }

        /// <summary>
        /// Whether to run all of the scene's steps, after those of <see cref="Test"/>, before applying <see cref="Steps"/> and <see cref="Sliders"/>.
        /// </summary>
        public bool RunAllSteps { get; init; }

        /// <summary>
        /// Steps to perform, by their text, in order. Toggle steps switch on the first time.
        /// </summary>
        public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Values for slider steps, by their description.
        /// </summary>
        public IReadOnlyDictionary<string, double> Sliders { get; init; } = new Dictionary<string, double>();

        public BenchmarkCase(string name, Type sceneType)
        {
            if (!typeof(TestScene).IsAssignableFrom(sceneType))
                throw new ArgumentException($"{sceneType} is not a {nameof(TestScene)}.", nameof(sceneType));

            Name = name;
            SceneType = sceneType;
        }
    }
}
