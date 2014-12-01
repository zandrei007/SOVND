﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ninject;
using Ninject.Extensions.Factory;
using SOVND.Client.Settings;
using SOVND.Lib.Handlers;
using SOVND.Lib.Models;
using SOVND.Lib.Settings;
using SOVND.Client.ViewModels;
using SpotifyClient;
using SOVND.Client.Modules;

namespace SOVND.Client
{
    static class Program
    {
        [STAThread]
        public static void Main()
        {
            IKernel kernel = new StandardKernel();
            kernel.Bind<IMQTTSettings>().To<SovndMqttSettings>();
            kernel.Bind<IChannelHandlerFactory>().ToFactory();
            kernel.Bind<IPlaylistProvider>().To<PlaylistProvider>();
            kernel.Bind<IChatProvider>().To<ChatProvider>();
            kernel.Bind<ISettingsProvider>().To<FilesystemSettingsProvider>();
            kernel.Bind<IFileLocationProvider>().To<AppDataLocationProvider>();
            kernel.Bind<IAppName>().To<AppName>();

            kernel.Bind<SyncHolder>().ToSelf().InSingletonScope();

            kernel.Bind<NowPlayingHandler>().ToSelf().InSingletonScope();
            kernel.Bind<SovndClient>().ToSelf().InSingletonScope();

            // Instantiating this class checks settings and shows UI if they're not set
            kernel.Get<CheckSettings>();

            // Instantiating this initializes Spotify
            kernel.Get<StartSpotify>();

            kernel.Get<MainWindow>().Show();

            var app = kernel.Get<App>();
            app.Run();
        }
    }

    public class CheckSettings
    {
        public CheckSettings(ISettingsProvider settings)
        {
            if (!settings.AuthSettingsSet())
            {
                SettingsWindow w = new SettingsWindow();
                var settingsViewModel = new SettingsViewModel(settings.GetAuthSettings());
                w.DataContext = settingsViewModel;
                w.ShowDialog();
            }
        }
    }

    public class StartSpotify
    {
        public StartSpotify(IAppName _appname, ISettingsProvider settings)
        {
            var _auth = settings.GetAuthSettings();

            Spotify.Initialize();
            if (!Spotify.Login(File.ReadAllBytes("spotify_appkey.key"), _appname.Name, _auth.SpotifyUsername, _auth.SpotifyPassword))
                throw new Exception("Login failure");

            while (!Spotify.Ready())
                Thread.Sleep(100);
        }
    }
}
