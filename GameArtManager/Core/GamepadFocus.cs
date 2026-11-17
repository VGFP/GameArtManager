using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace GameArtManager.Core
{
    /// <summary>
    /// Gamepad → focus translation for Windows PCs. Automatic gamepad UI navigation
    /// (XY focus, A/B activation) is an Xbox device family behaviour: on desktop
    /// Windows — handhelds and the full screen experience — RequiresPointerMode is
    /// ignored and a gamepad only surfaces as VirtualKey.Gamepad* key events, so the
    /// pages feed those events through this helper.
    /// </summary>
    public static class GamepadFocus
    {
        /// <summary>Handles one gamepad key event: D-pad / left stick move focus,
        /// A activates the focused control. Returns true when the key was handled;
        /// B is left to the caller (page-specific back behaviour).</summary>
        public static bool HandleKey(VirtualKey key, Func<FrameworkElement, bool> activateItem)
        {
            switch (key)
            {
                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.GamepadLeftThumbstickLeft:
                    return TryMoveFocus(FocusNavigationDirection.Left);
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.GamepadLeftThumbstickRight:
                    return TryMoveFocus(FocusNavigationDirection.Right);
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    return TryMoveFocus(FocusNavigationDirection.Up);
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    return TryMoveFocus(FocusNavigationDirection.Down);
                case VirtualKey.GamepadA:
                    return ActivateFocused(activateItem);
                default:
                    return false;
            }
        }

        /// <summary>Data item of a list container — null when the container belongs
        /// to another list or is not realized.</summary>
        public static object ItemOf(ItemsControl list, FrameworkElement container)
        {
            if (list == null || container == null) return null;
            if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(container), list)) return null;
            return list.ItemFromContainer(container);
        }

        private static bool TryMoveFocus(FocusNavigationDirection direction)
        {
            try
            {
                return FocusManager.TryMoveFocus(direction);
            }
            catch
            {
                // focus navigation unavailable (nothing focusable on screen)
                return false;
            }
        }

        private static bool ActivateFocused(Func<FrameworkElement, bool> activateItem)
        {
            var focused = FocusManager.GetFocusedElement() as FrameworkElement;
            if (focused == null) return false;

            // list item containers first (they have no useful invoke pattern)
            var container = FindAncestor<ListViewItem>(focused)
                            ?? (FrameworkElement)FindAncestor<GridViewItem>(focused);
            if (container != null)
                return activateItem != null && activateItem(container);

            // buttons / toggles: the automation Invoke pattern is the click
            try
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(focused);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                {
                    invoke.Invoke();
                    return true;
                }
            }
            catch
            {
                // element has no invoke pattern
            }
            return false;
        }

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            var node = start;
            while (node != null && !(node is T))
                node = VisualTreeHelper.GetParent(node);
            return node as T;
        }
    }
}
