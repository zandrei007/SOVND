using System.Collections.Generic;
using Anotar.NLog;
using Newtonsoft.Json;
using SOVND.Client.Util;
using SOVND.Lib.Handlers;
using SOVND.Lib.Models;
using SpotifyClient;

namespace SOVND.Client.Modules
{
    public class SovndClient : SOVNDModule
    {
        private readonly IChannelHandlerFactory _chf;

        private readonly string Username;

        public ChannelHandler SubscribedChannelHandler;

        private Dictionary<string, int> votes = new Dictionary<string, int>();
        private Dictionary<string, bool> uservotes = new Dictionary<string, bool>();

        public SovndClient(AuthPair auth, IChannelHandlerFactory chf, ChannelDirectory channels) : base(auth)
        {
            _chf = chf;

            Username = auth.Settings.GetSettings().SOVNDUsername;
            Logging.SetupLogging(Username);

            // TODO Track channel list
            // TODO Track playlist for channel

            // On /channel/info -> track channel list
            // On /selectedchannel/ nowplaying,playlist,stats,chat -> track playlist, subscribed channel details

            // TODO: Need to move all of this to somewhere channel specific

            // TODO: We don't need to be subbed to this all the time, just when browsing for channels
            On["/{channel}/info"] = _ =>
            {
                Channel channel = JsonConvert.DeserializeObject<Channel>(_.Message);

                channels.AddChannel(channel);
            };
        }

        internal void SendChat(string text)
        {
            if (SubscribedChannelHandler != null)
                Publish(string.Format("/user/{0}/{1}/chat", Username, SubscribedChannelHandler.Name), text);
            else
                LogTo.Warn("Cannot send chat: not subscribed to a channel");
        }

        public bool RegisterChannel(string name, string description, string image)
        {
            var channel = new Channel
            {
                Name = name,
                Description = description
            };
            return RegisterChannel(channel);
        }

        public bool RegisterChannel(Channel channel)
        {
            // TODO: Detect success or figure out a way to come close (eg check channels that have been registered locally)

            if (channel == null || string.IsNullOrWhiteSpace(channel.Name))
                return false;

            var msg = JsonConvert.SerializeObject(channel);

            Publish(string.Format("/user/{0}/register/{1}", Username, channel.Name), msg);
            return true;
        }

        internal void SubscribeToChannel(string channel)
        {
            if (SubscribedChannelHandler != null)
                SubscribedChannelHandler.ShutdownHandler();

            SubscribedChannelHandler = _chf.CreateChannelHandler(channel);
            SubscribedChannelHandler.Subscribe();
        }

        public void AddTrack(Track track)
        {
            AddTrack(track.SongID);
        }

        public void AddTrack(string track)
        {
            if (SubscribedChannelHandler != null && SubscribedChannelHandler.Name != null)
            {
                Publish(string.Format("/user/{0}/{1}/songs/{2}", Username, SubscribedChannelHandler.Name, track), "vote");
            }
            else
                LogTo.Warn("Not subscribed to a channel or channel subscription is malformed (null or Name null)");
        }

        public void DeleteSong(Song item)
        {
            if (SubscribedChannelHandler != null && SubscribedChannelHandler.Name != null)
            {
                LogTo.Info("[{1}] Asking server to delete song {0}", item.track.Loaded ? item.track.Name : item.SongID, SubscribedChannelHandler.Name);
                Publish(string.Format("/user/{0}/{1}/songs/{2}", Username, SubscribedChannelHandler.Name, item.SongID), "remove");
            }
            else
                LogTo.Warn("Not subscribed to a channel or channel subscription is malformed (null or Name null)");
        }

        public void BlockSong(Song item)
        {
            if (SubscribedChannelHandler != null && SubscribedChannelHandler.Name != null)
            {
                LogTo.Info("Blocking song {0} on channel {1}", item.track.Loaded ? item.track.Name : item.SongID, SubscribedChannelHandler.Name);
                Publish(string.Format("/user/{0}/{1}/songs/{2}", Username, SubscribedChannelHandler.Name, item.SongID), "block");
            }
            else
                LogTo.Warn("Not subscribed to a channel or channel subscription is malformed (null or Name null)");
        }

        public void ReportSong(Song item)
        {
            if (SubscribedChannelHandler != null && SubscribedChannelHandler.Name != null)
            {
                LogTo.Info("Reporting song {0} on channel {1}", item.track.Loaded ? item.track.Name : item.SongID, SubscribedChannelHandler.Name);
                Publish(string.Format("/user/{0}/{1}/songs/{2}", Username, SubscribedChannelHandler.Name, item.SongID), "report");
            }
            else
                LogTo.Warn("Not subscribed to a channel or channel subscription is malformed (null or Name null)");
        }
    }
}