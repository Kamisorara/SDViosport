using System;
using System.IO;
using AVFoundation;
using Foundation;
using UIKit;
using SDViOS.Diagnostics;
using SDViOS.Loader;

namespace SDViOS
{
    [Register("AppDelegate")]
    public class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
        {
            try
            {
                ConfigureAudioSession();

                NSNotificationCenter.DefaultCenter.AddObserver(
                    new NSString("UISceneWillConnectNotification"),
                    _ => UIApplication.SharedApplication.BeginInvokeOnMainThread(GameHost.LinkGameWindowToScene)
                );
                NSNotificationCenter.DefaultCenter.AddObserver(
                    new NSString("UISceneDidActivateNotification"),
                    _ => UIApplication.SharedApplication.BeginInvokeOnMainThread(GameHost.LinkGameWindowToScene)
                );
                NSNotificationCenter.DefaultCenter.AddObserver(
                    UIApplication.DidBecomeActiveNotification,
                    _ => UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                    {
                        ConfigureAudioSession();
                        GameHost.LinkGameWindowToScene();
                    })
                );

                NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification, _ => EngineLogger.Flush());
                NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidEnterBackgroundNotification, _ => GameHost.OnDidEnterBackground());
                NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillTerminateNotification, _ => GameHost.OnDidEnterBackground());

                GameHost.InitializeFileSystem();
                EngineLogger.Log("[AppDelegate] FinishedLaunching: Checking game files...");

                if (GameHost.TryFindGameBinary(out string sdvPath, out _))
                {
                    EngineLogger.Log($"Game files located at: {sdvPath}. Scheduling launch on main runloop...");
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            GameHost.Launch(Array.Empty<string>());
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogFatal("GameHost Launch on MainThread", ex);
                            ShowErrorScreen(ex);
                        }
                    });
                }
                else
                {
                    EngineLogger.LogWarning("Game files missing. Displaying setup interface.");
                    ShowMissingFilesScreen();
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("AppDelegate FinishedLaunching", ex);
                ShowErrorScreen(ex);
            }

            return true;
        }

        private void ShowMissingFilesScreen()
        {
            var scene = GameHost.GetActiveWindowScene();
            Window = scene != null ? new UIWindow(scene) : new UIWindow(UIScreen.MainScreen.Bounds);
            if (scene != null && Window.WindowScene == null)
            {
                Window.WindowScene = scene;
            }

            var vc = new UIViewController();
            vc.View!.BackgroundColor = UIColor.FromRGB(24, 30, 42);

            nfloat width = Window.Bounds.Width;
            nfloat height = Window.Bounds.Height;

            var labelTitle = new UILabel(new CoreGraphics.CGRect(20, 50, width - 40, 40))
            {
                Text = "Stardew Valley (PC Port)",
                TextColor = UIColor.White,
                Font = UIFont.BoldSystemFontOfSize(24),
                TextAlignment = UITextAlignment.Center
            };

            var labelStatus = new UILabel(new CoreGraphics.CGRect(30, 100, width - 60, 110))
            {
                Text = "Game files not found!\n\nPlease copy your PC Stardew Valley files into the iOS Files app:\nOn My iPhone > Stardew Valley\n\nRequired: Stardew Valley.dll and Content folder.",
                TextColor = UIColor.FromRGB(220, 220, 220),
                Font = UIFont.SystemFontOfSize(15),
                Lines = 0,
                TextAlignment = UITextAlignment.Center
            };

            var btnReload = new UIButton(UIButtonType.System)
            {
                Frame = new CoreGraphics.CGRect((width - 220) / 2, 230, 220, 46),
                BackgroundColor = UIColor.FromRGB(46, 139, 87),
                Layer = { CornerRadius = 10 }
            };
            btnReload.SetTitle("Check Files & Start Game", UIControlState.Normal);
            btnReload.SetTitleColor(UIColor.White, UIControlState.Normal);
            btnReload.TitleLabel.Font = UIFont.BoldSystemFontOfSize(16);
            btnReload.TouchUpInside += (s, e) =>
            {
                if (GameHost.TryFindGameBinary(out _, out _))
                {
                    GameHost.Launch(Array.Empty<string>());
                }
                else
                {
                    var alert = UIAlertController.Create("Still Missing", "Stardew Valley.dll was not found yet.\n\nPath checked:\nFiles app > On My iPhone > Stardew Valley", UIAlertControllerStyle.Alert);
                    alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                    vc.PresentViewController(alert, true, null);
                }
            };

            vc.View.AddSubviews(labelTitle, labelStatus, btnReload);
            Window.RootViewController = vc;
            Window.Hidden = false;
            Window.MakeKeyAndVisible();
        }

        private void ShowErrorScreen(Exception ex)
        {
            InvokeOnMainThread(() =>
            {
                var scene = GameHost.GetActiveWindowScene();
                Window = scene != null ? new UIWindow(scene) : new UIWindow(UIScreen.MainScreen.Bounds);
                if (scene != null && Window.WindowScene == null)
                {
                    Window.WindowScene = scene;
                }

                Window.WindowLevel = UIWindowLevel.Alert + 1000;
                var vc = new UIViewController();
                vc.View!.BackgroundColor = UIColor.FromRGB(45, 12, 12);

                var realEx = ex;
                while (realEx.InnerException != null)
                {
                    realEx = realEx.InnerException;
                }

                nfloat width = Window.Bounds.Width;
                nfloat height = Window.Bounds.Height;

                var labelTitle = new UILabel(new CoreGraphics.CGRect(20, 50, width - 40, 30))
                {
                    Text = "CRASH REPORT",
                    TextColor = UIColor.Red,
                    Font = UIFont.BoldSystemFontOfSize(20),
                    TextAlignment = UITextAlignment.Center
                };

                var tv = new UITextView(new CoreGraphics.CGRect(20, 90, width - 40, height - 110))
                {
                    Text = $"Error: {realEx.GetType().Name}\nMessage: {realEx.Message}\n\n--- Call Stack ---\n{realEx.StackTrace}\n\n--- Full Details ---\n{ex}",
                    TextColor = UIColor.White,
                    Font = UIFont.SystemFontOfSize(12),
                    Editable = false,
                    BackgroundColor = UIColor.FromRGB(20, 5, 5)
                };

                vc.View.AddSubviews(labelTitle, tv);
                Window.RootViewController = vc;
                Window.Hidden = false;
                Window.MakeKeyAndVisible();
            });
        }

        public override void OnActivated(UIApplication application)
        {
            try
            {
                EngineLogger.Log("[AppDelegate] OnActivated");
                GameHost.LinkGameWindowToScene();
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[AppDelegate] OnActivated error: {ex}");
            }
        }

        public override void DidEnterBackground(UIApplication application)
        {
            EngineLogger.Log("[AppDelegate] DidEnterBackground: pausing display link and flushing logs.");
            GameHost.OnDidEnterBackground();
        }

        public override void WillEnterForeground(UIApplication application)
        {
            try
            {
                EngineLogger.Log("[AppDelegate] WillEnterForeground: resuming display link.");
                ConfigureAudioSession();
                GameHost.OnWillEnterForeground();
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[AppDelegate] WillEnterForeground error: {ex}");
            }
        }

        public override void ReceiveMemoryWarning(UIApplication application)
        {
            try
            {
                long beforeMb = GC.GetTotalMemory(false) / (1024 * 1024);
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                long afterMb = GC.GetTotalMemory(false) / (1024 * 1024);
                EngineLogger.LogWarning($"[AppDelegate] iOS ReceiveMemoryWarning! Forced LOH compacting GC: managed {beforeMb} MB -> {afterMb} MB");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[AppDelegate] ReceiveMemoryWarning GC error: {ex.Message}");
            }
        }

        public override void WillTerminate(UIApplication application)
        {
            EngineLogger.Log("[AppDelegate] WillTerminate");
            GameHost.OnDidEnterBackground();
        }

        public static void ConfigureAudioSession()
        {
            try
            {
                var session = AVAudioSession.SharedInstance();
                session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionCategoryOptions.MixWithOthers);
                var err = session.SetActive(true);
                if (err != null)
                {
                    EngineLogger.LogWarning($"[AppDelegate] AVAudioSession.SetActive warning: {err.LocalizedDescription}");
                }
                else
                {
                    EngineLogger.Log("[AppDelegate] AVAudioSession configured successfully (Playback, MixWithOthers).");
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[AppDelegate] Error configuring AVAudioSession: {ex.Message}");
            }
        }
    }
}
