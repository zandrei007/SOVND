﻿/*-
 * Copyright (c) 2014 Software Development Solutions, Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
 * OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
 * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
 * OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using Anotar.NLog;
using libspotifydotnet.libspotify;
using NAudio.Wave;
using SpotifyClient;

namespace SOVND.Client.Audio
{
    public class SpotifyTrackDataPipe : IDisposable
    {
        private static bool _loaded;
        private static BufferedWaveProvider _wave;
        private static WaveFormat _waveFormat;
        private static int _jitter;
        private static bool _bufferset;

        private Action _stop;
        private Action<BufferedWaveProvider> _newFormat;
        private Action _init;

        public SpotifyTrackDataPipe()
        {
            LogTo.Trace("STDP: Constructor");
            Session.OnAudioDataArrived = Session_OnAudioDataArrived;
            Session.OnAudioStreamComplete = Session_OnAudioStreamComplete;
            Session.AudioBufferStats = Session_AudioBufferStats;
        }

        public void Dispose() { } // For Fody.Janitor (https://github.com/Fody/Janitor)

        public void DisposeManaged()
        {
            LogTo.Trace("STDP: DisposeManaged()");

            if (_loaded)
            {
                LogTo.Trace("STDP: DisposeManaged(): Session.UnloadPlayer()");
                Session.UnloadPlayer();
                _stop();
                _loaded = false;
            }
        }

        public void StartStreaming(IntPtr _trackPtr, Action init, Action<BufferedWaveProvider> newFormat, Action stop)
        {
            StartStreaming(DateTime.MinValue, _trackPtr, init, newFormat, stop);
        }

        public void StartStreaming(DateTime startTime, IntPtr _trackPtr, Action init, Action<BufferedWaveProvider> newFormat, Action stop)
        {
            LogTo.Trace("STDP: StartStreaming()");
            _init = init;
            _stop = stop;
            _newFormat = newFormat;

            var error = Session.LoadPlayer(_trackPtr);
            while (error == sp_error.IS_LOADING)
            {
                Thread.Sleep(150);
                LogTo.Warn("Can't play just yet, track not loaded");
                error = Session.LoadPlayer(_trackPtr);
            }

            if (error != sp_error.OK)
            {
                LogTo.Error("[Spotify] Streaming error: \{sp_error_message(error)}");
                return;
            }   

            _loaded = true;
            sp_availability avail = sp_track_get_availability(Session.SessionPtr, _trackPtr);

            if (avail != sp_availability.SP_TRACK_AVAILABILITY_AVAILABLE)
            {
                LogTo.Warn("Track is unavailable: {0}", avail);
                return;
            }

            int seektime = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            if (seektime < -250)
            {
                Session.PrefetchTrack(_trackPtr); // I have no clue how long this takes
                LogTo.Error("STDP: We have some time ({0}ms), prefetching track", seektime);
            }

            seektime = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            if ((seektime < 0) && (startTime != DateTime.MinValue))
            {
                LogTo.Error("STDP: StartStreaming(): Playing in {0}ms", seektime);
                Thread.Sleep(-seektime);
            }

            _bufferset = false;
            LogTo.Trace("STDP: StartStreaming(): Session.Play()");
            Session.Play();

            if (seektime < 0)
                return;

            if (startTime != DateTime.MinValue)
            {
                LogTo.Trace("STDP: StartStreaming(): Session.Seek(\{seektime})");
                Session.Seek(seektime);
            }
        }

        private void Session_AudioBufferStats(ref sp_audio_buffer_stats obj)
        {
            if (_wave == null)
            {
                //LogTo.Trace("STDP: Session_AudioBufferStats: _wave null");
                return;
            }

            obj.samples = _wave.BufferedBytes / 2; // (16 bit audio -> 2 bytes per sample)
            obj.stutter = _jitter;
            _jitter = 0;
        }

        private void Session_OnAudioStreamComplete(object obj)
        {
            LogTo.Trace("STDP: (NICE TRY) Session_OnAudioStreamComplete");
            Dispose();
        }

        private int Session_OnAudioDataArrived(byte[] buffer, sp_audioformat format)
        {
            if ((!_bufferset) || // Buffer hasn't been setup yet
                (format.channels != _waveFormat.Channels) || (format.sample_rate != _waveFormat.SampleRate)) // New audio format
                SetAudioFormat(format);

            if (buffer.Length == 0)
            {
                _wave.ClearBuffer();
                return 0;
            }

            // Try to keep buffer mostly full
            //if (_wave.BufferedBytes < _wave.BufferLength - 40000) // 40000 samples = ~1s
            //    _jitter++;

            if (buffer.Length > _wave.BufferLength - _wave.BufferedBytes)
                return 0;

            _wave.AddSamples(buffer, 0, buffer.Length);

            return buffer.Length;
        }

        private static void SetupBuffer(sp_audioformat format)
        {
            LogTo.Trace("STDP: SetupBuffer()");
            _bufferset = true;
            if ((_wave != null) && (_waveFormat != null) && // Buffer is already setup
                (format.channels == _waveFormat.Channels) && (format.sample_rate == _waveFormat.SampleRate)) // Format is the same
            {
                LogTo.Trace("STDP: SetupBuffer(): _wave.ClearBuffer()");
                _wave.ClearBuffer();
                return;
            }

            LogTo.Trace("STDP: SetupBuffer(): _wave: new()");
            _waveFormat = new WaveFormat(format.sample_rate, 16, format.channels);
            _wave = new BufferedWaveProvider(_waveFormat);
            _wave.BufferDuration = TimeSpan.FromSeconds(15);
        }

        private void SetAudioFormat(sp_audioformat format)
        {
            LogTo.Trace("STDP: SetAudioFormat()");
            SetupBuffer(format);
            _newFormat(_wave);
            _init();
        }
    }
}