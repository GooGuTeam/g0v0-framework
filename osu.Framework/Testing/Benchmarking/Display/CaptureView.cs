// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// A captured frame loaded from a PNG, with a caption.
    /// </summary>
    internal partial class CaptureView : CompositeDrawable
    {
        private readonly SpriteText caption;
        private readonly Sprite sprite;
        private readonly SpriteText placeholder;

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        private string? path;
        private Texture? texture;

        /// <summary>
        /// Whether an image is being loaded.
        /// </summary>
        public bool IsLoading { get; private set; }

        public CaptureView()
        {
            InternalChildren = new Drawable[]
            {
                caption = new SpriteText
                {
                    Font = BenchmarkColours.Font(13, bold: true),
                    Colour = BenchmarkColours.TEXT_DIM,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 20 },
                    Child = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = 4,
                        BorderColour = BenchmarkColours.BORDER,
                        BorderThickness = 1,
                        Children = new Drawable[]
                        {
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = Colour4.Black,
                            },
                            placeholder = new SpriteText
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Font = BenchmarkColours.Font(13),
                                Colour = BenchmarkColours.TEXT_FAINT,
                            },
                            sprite = new Sprite
                            {
                                RelativeSizeAxes = Axes.Both,
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                FillMode = FillMode.Fit,
                            },
                        },
                    },
                },
            };
        }

        public void Show(string title, string? imagePath)
        {
            caption.Text = title;

            if (imagePath == path)
                return;

            path = imagePath;
            setTexture(null);
            IsLoading = false;

            if (imagePath == null || !File.Exists(imagePath))
            {
                placeholder.Text = "No capture";
                return;
            }

            placeholder.Text = "Loading…";
            IsLoading = true;

            Task.Run(() =>
            {
                using (var stream = File.OpenRead(imagePath))
                    return Image.Load<Rgba32>(stream);
            }).ContinueWith(t => Schedule(() =>
            {
                if (imagePath != path)
                {
                    if (t.IsCompletedSuccessfully)
                        t.GetResultSafely().Dispose();
                    return;
                }

                IsLoading = false;

                if (!t.IsCompletedSuccessfully)
                {
                    placeholder.Text = "The capture could not be read";
                    return;
                }

                var image = t.GetResultSafely();
                var loaded = renderer.CreateTexture(image.Width, image.Height);
                loaded.SetData(new TextureUpload(image));

                placeholder.Text = string.Empty;
                setTexture(loaded);
            }));
        }

        private void setTexture(Texture? value)
        {
            sprite.Texture = value;
            texture?.Dispose();
            texture = value;
        }

        protected override void Dispose(bool isDisposing)
        {
            texture?.Dispose();
            base.Dispose(isDisposing);
        }
    }
}
