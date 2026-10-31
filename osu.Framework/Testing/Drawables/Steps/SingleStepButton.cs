// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;

namespace osu.Framework.Testing.Drawables.Steps
{
    public partial class SingleStepButton : StepButton
    {
        public new required Action Action { get; init; }

        protected override IconUsage DefaultIcon => FontAwesome.Solid.Play;

        public SingleStepButton()
        {
            base.Action = clickAction;
        }

        private void clickAction()
        {
            Action();
            Success();
        }
    }
}
