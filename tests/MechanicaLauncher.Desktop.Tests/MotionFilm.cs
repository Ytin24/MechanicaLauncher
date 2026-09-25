using System;
using System.IO;
using MechanicaLauncher.Desktop;
using Nitidus;
using Nitidus.Native;

internal static partial class Program
{
    private static void MotionFilm(LauncherModel model, LauncherView presentation, HeadlessHost host, Pump context)
    {
        var view = presentation.View;
        double time = 1;
        int frame = 0;
        Directory.CreateDirectory(Path.Combine(output, "film"));
        void Record(double seconds)
        {
            int count = (int)Math.Round(seconds * 30);
            for (int i = 0; i < count; i++)
            {
                context.Drain(); view.Animations.ReducedMotion = false;
                host.Render(time += 1d / 30);
                Capture(host, $"film/{frame++:D4}");
            }
        }
        void Click(string name)
        {
            var b = UiTransform.VisualBounds(view.Find(name));
            host.Click(b.X + b.Width / 2, b.Y + b.Height / 2);
        }
        Record(.3);
        Click("navHome"); Record(.9);
        var art = UiTransform.VisualBounds(view.Find("hero"));
        host.Send(new(InputType.Move, art.X + art.Width / 2, art.Y + art.Height / 2, 0)); Record(.5);
        host.Send(new(InputType.Move, -1, -1, 0)); Record(.4);
        Click("navLibrary"); Record(.8);
        model.EditInstance(model.SelectedInstance!.Id); Record(.8);
        Click("navHome"); Record(.8);
        Click("instancePicker"); Record(.8);
        Click("dialogClose"); Record(.5);
        var play = UiTransform.VisualBounds(view.Find("play"));
        host.Send(new(InputType.Move, play.X + 40, play.Y + 20, 0)); Record(.2);
        host.Send(new(InputType.Down, play.X + 40, play.Y + 20, 0)); Record(.15);
        host.Send(new(InputType.Move, -1, -1, 0));
        host.Send(new(InputType.Up, -1, -1, 0)); Record(.65);
        Check(!model.DialogOpen && model.Page == "home" && !view.Animations.NeedsFrames, "recorded motion returns to a fully idle home screen");
    }
}
