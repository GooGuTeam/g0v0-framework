// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Graphics.Containers;

namespace osu.Framework.Graphics.Visualisation
{
    /// <summary>
    /// A layer which hosts tool windows and keeps track of their stacking order.
    /// </summary>
    /// <remarks>
    /// A window is moved to the front of the layer when it is shown, and windows can request to be moved to the front
    /// when they are interacted with (see <see cref="ToolWindow.BringToFront"/>).
    /// </remarks>
    internal partial class ToolWindowContainer : Container<Drawable>
    {
        /// <summary>
        /// The hosted windows, ordered from the back of the layer to the front.
        /// </summary>
        private readonly List<Drawable> stackingOrder = new List<Drawable>();

        public ToolWindowContainer()
        {
            RelativeSizeAxes = Axes.Both;
        }

        /// <summary>
        /// Moves the window containing <paramref name="drawable"/> to the front of the layer.
        /// </summary>
        /// <param name="drawable">A drawable which is either a direct child of this layer, or nested inside one.</param>
        public void BringToFront(Drawable drawable)
        {
            while (drawable.Parent != this)
            {
                if (drawable.Parent == null)
                    return;

                drawable = drawable.Parent;
            }

            if (!stackingOrder.Remove(drawable))
                return;

            stackingOrder.Add(drawable);
            updateDepths();
        }

        protected override void AddInternal(Drawable drawable)
        {
            base.AddInternal(drawable);

            stackingOrder.Add(drawable);

            // windows which are shown after having been hidden should come to the front, just like newly created ones.
            if (drawable is VisibilityContainer window)
                window.State.BindValueChanged(state => { if (state.NewValue == Visibility.Visible) BringToFront(window); });

            updateDepths();
        }

        protected internal override bool RemoveInternal(Drawable drawable, bool disposeImmediately)
        {
            if (!base.RemoveInternal(drawable, disposeImmediately))
                return false;

            stackingOrder.Remove(drawable);
            updateDepths();
            return true;
        }

        protected internal override void ClearInternal(bool disposeChildren = true)
        {
            base.ClearInternal(disposeChildren);
            stackingOrder.Clear();
        }

        private void updateDepths()
        {
            // depth cannot be assigned directly while a drawable is parented, so the container has to reorder its own children.
            // lower depths are drawn in front, hence the inverted index.
            for (int i = 0; i < stackingOrder.Count; i++)
                ChangeInternalChildDepth(stackingOrder[i], stackingOrder.Count - 1 - i);
        }
    }
}
