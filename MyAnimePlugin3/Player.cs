﻿#region Copyright (C) 2005-2011 Team MediaPortal

// Copyright (C) 2005-2011 Team MediaPortal
// http://www.team-mediaportal.com
// 
// MediaPortal is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// MediaPortal is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with MediaPortal. If not, see <http://www.gnu.org/licenses/>.


#endregion


using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using DirectShowLib;
using DShowNET.Helper;
using MediaPortal.Common.Utils;
using MediaPortal.GUI.Library;
using MediaPortal.MusicPlayer.BASS;
using MediaPortal.Player;
using MediaPortal.Profile;

using Config = MediaPortal.Configuration.Config;

namespace MyAnimePlugin3
{
    public class Player : MediaPortal.Player.VideoPlayerVMR9
    {

        protected override bool GetInterfaces()
        {
            GetInterface = true;
            try
            {
                graphBuilder = (IGraphBuilder)new FilterGraph();
                _rotEntry = new DsROTEntry((IFilterGraph)graphBuilder);
                int hr;
                filterConfig = GetFilterConfiguration();
                filterCodec = GetFilterCodec();
                if (filterConfig.bAutoDecoderSettings)
                {
                    AutoRenderingCheck = true;
                    return base.GetInterfaces();
                }


                string extension = Path.GetExtension(m_strCurrentFile).ToLowerInvariant();


                GUIMessage msg;
                if (extension == ".mpls" || extension == ".bdmv")
                {
                    filterConfig.bForceSourceSplitter = false;
                    filterConfig = GetFilterConfigurationBD();
                }

                //Manually add codecs based on file extension if not in auto-settings
                // switch back to directx fullscreen mode
                Log.Info("VideoPlayer9: Enabling DX9 exclusive mode");
                msg = new GUIMessage(GUIMessage.MessageType.GUI_MSG_SWITCH_FULL_WINDOWED, 0, 0, 0, 1, 0, null);
                GUIWindowManager.SendMessage(msg);

                // add the VMR9 in the graph
                // after enabeling exclusive mode, if done first it causes MediPortal to minimize if for example the "Windows key" is pressed while playing a video
                if (extension != ".dts" && extension != ".mp3" && extension != ".mka" && extension != ".ac3")
                {
                    if (g_Player._mediaInfo != null && !g_Player._mediaInfo.MediaInfoNotloaded &&
                        !g_Player._mediaInfo.hasVideo)
                    {
                        AudioOnly = true;
                    }
                    else
                    {
                        Vmr9 = new VMR9Util();
                        Vmr9.AddVMR9(graphBuilder);
                        Vmr9.Enable(false);
                    }
                }
                else
                {
                    AudioOnly = true;
                }

                if (filterConfig.strsplitterfilter == LAV_SPLITTER_FILTER_SOURCE && filterConfig.bForceSourceSplitter)
                {
                    LoadLAVSplitter(LAV_SPLITTER_FILTER_SOURCE);
                    hr = graphBuilder.AddFilter(_interfaceSourceFilter, LAV_SPLITTER_FILTER_SOURCE);
                    DsError.ThrowExceptionForHR(hr);

                    Log.Debug("VideoPlayer9: Add LAVSplitter Source to graph");

                    IFileSourceFilter interfaceFile = (IFileSourceFilter) _interfaceSourceFilter;
                    hr = interfaceFile.Load(m_strCurrentFile, null);

                    if (hr != 0)
                    {
                        Error.SetError("Unable to play movie", "Unable build graph for VMR9");
                        Cleanup();
                        return false;
                    }
                }
                else
                {
                    _interfaceSourceFilter = filterConfig.bForceSourceSplitter
                        ? DirectShowUtil.AddFilterToGraph(graphBuilder, filterConfig.strsplitterfilter)
                        : null;
                    if (_interfaceSourceFilter == null && !filterConfig.bForceSourceSplitter)
                    {
                        graphBuilder.AddSourceFilter(m_strCurrentFile, null, out _interfaceSourceFilter);
                    }
                    else
                    {
                        try
                        {
                            int result = ((IFileSourceFilter) _interfaceSourceFilter).Load(m_strCurrentFile, null);
                            if (result != 0)
                            {
                                graphBuilder.RemoveFilter(_interfaceSourceFilter);
                                DirectShowUtil.ReleaseComObject(_interfaceSourceFilter);
                                _interfaceSourceFilter = null;
                                graphBuilder.AddSourceFilter(m_strCurrentFile, null, out _interfaceSourceFilter);
                            }
                        }

                        catch (Exception ex)
                        {
                            Log.Error(
                                "VideoPlayer9: Exception loading Source Filter setup in setting in DShow graph , try to load by merit",
                                ex);
                            graphBuilder.RemoveFilter(_interfaceSourceFilter);
                            DirectShowUtil.ReleaseComObject(_interfaceSourceFilter);
                            _interfaceSourceFilter = null;
                            graphBuilder.AddSourceFilter(m_strCurrentFile, null, out _interfaceSourceFilter);
                        }
                    }

                    //Detection of File Source (Async.) as source filter, return true if found
                    IBaseFilter fileSyncbaseFilter = null;
                    DirectShowUtil.FindFilterByClassID(graphBuilder, ClassId.FilesyncSource, out fileSyncbaseFilter);
                    if (fileSyncbaseFilter == null)
                        graphBuilder.FindFilterByName("File Source (Async.)", out fileSyncbaseFilter);
                    if (fileSyncbaseFilter != null && filterConfig.bForceSourceSplitter)
                    {
                        FileSync = true;
                        DirectShowUtil.ReleaseComObject(fileSyncbaseFilter);
                        fileSyncbaseFilter = null;
                        if (filterConfig.strsplitterfilefilter == LAV_SPLITTER_FILTER)
                        {
                            LoadLAVSplitter(LAV_SPLITTER_FILTER);
                            hr = graphBuilder.AddFilter(Splitter, LAV_SPLITTER_FILTER);
                            DsError.ThrowExceptionForHR(hr);

                            Log.Debug("VideoPlayer9: Add LAVSplitter to graph");

                            if (hr != 0)
                            {
                                Error.SetError("Unable to play movie", "Unable build graph for VMR9");
                                Cleanup();
                                return false;
                            }
                        }
                        else
                        {
                            Splitter = DirectShowUtil.AddFilterToGraph(graphBuilder, filterConfig.strsplitterfilefilter);
                        }
                    }
                }

                // Add preferred video filters
                UpdateFilters("Video");

                //Add Audio Renderer
                if (filterConfig.AudioRenderer.Length > 0 && filterCodec._audioRendererFilter == null)
                {
                    filterCodec._audioRendererFilter = DirectShowUtil.AddAudioRendererToGraph(graphBuilder,
                        filterConfig.AudioRenderer, false);
                }

                #region load external audio streams

                // check if current "File" is a file... it could also be a URL
                // Directory.Getfiles, ... will other give us an exception
                if (File.Exists(m_strCurrentFile) && !AudioOnly)
                {
                    //load audio file (ac3, dts, mka, mp3) only with if the name matches partially with video file.
                    string[] audioFiles = Directory.GetFiles(Path.GetDirectoryName(m_strCurrentFile),
                        Path.GetFileNameWithoutExtension(m_strCurrentFile) + "*.*");
                    bool audioSwitcherLoaded = false;
                    foreach (string file in audioFiles)
                    {
                        switch (Path.GetExtension(file))
                        {
                            case ".mp3":
                            case ".dts":
                            case ".mka":
                            case ".ac3":
                                if (!audioSwitcherLoaded)
                                {
                                    IBaseFilter switcher = DirectShowUtil.GetFilterByName(graphBuilder,
                                        MEDIAPORTAL_AUDIOSWITCHER_FILTER);
                                    if (switcher != null)
                                    {
                                        DirectShowUtil.ReleaseComObject(switcher);
                                        switcher = null;
                                    }
                                    else
                                    {
                                        _audioSwitcher = DirectShowUtil.AddFilterToGraph(graphBuilder,
                                            MEDIAPORTAL_AUDIOSWITCHER_FILTER);
                                    }
                                    audioSwitcherLoaded = true;
                                }
                                _AudioSourceFilter = DirectShowUtil.AddFilterToGraph(graphBuilder, FILE_SYNC_FILTER);
                                int result = ((IFileSourceFilter) _AudioSourceFilter).Load(file, null);

                                //Force using LAVFilter
                                _AudioExtSplitterFilter = DirectShowUtil.AddFilterToGraph(graphBuilder,
                                    LAV_SPLITTER_FILTER);

                                if (result != 0 || _AudioExtSplitterFilter == null)
                                {
                                    if (_AudioSourceFilter != null)
                                    {
                                        graphBuilder.RemoveFilter(_AudioSourceFilter);
                                        DirectShowUtil.ReleaseComObject(_AudioSourceFilter);
                                        _AudioSourceFilter = null;
                                    }
                                    if (_AudioExtSplitterFilter != null)
                                    {
                                        graphBuilder.RemoveFilter(_AudioExtSplitterFilter);
                                        DirectShowUtil.ReleaseComObject(_AudioExtSplitterFilter);
                                        _AudioExtSplitterFilter = null;
                                    }
                                    //Trying Add Audio decoder in graph
                                    InvokePrivateAddFilterToGraphAndRelease(filterConfig.Audio);
                                    graphBuilder.RenderFile(file, string.Empty);
                                    Log.Debug("VideoPlayerVMR9 : External audio file loaded \"{0}\"", file);
                                    AudioExternal = true;
                                    break;
                                }

                                //Add Audio decoder in graph
                                _AudioExtFilter = DirectShowUtil.AddFilterToGraph(graphBuilder, filterConfig.Audio);

                                //Connect Filesource with the splitter
                                IPin pinOutAudioExt1 = DsFindPin.ByDirection((IBaseFilter) _AudioSourceFilter,
                                    PinDirection.Output, 0);
                                IPin pinInAudioExt2 = DsFindPin.ByDirection((IBaseFilter) _AudioExtSplitterFilter,
                                    PinDirection.Input, 0);
                                hr = graphBuilder.Connect(pinOutAudioExt1, pinInAudioExt2);

                                //Connect Splitter with the Audio Decoder
                                IPin pinOutAudioExt3 = DsFindPin.ByDirection((IBaseFilter) _AudioExtSplitterFilter,
                                    PinDirection.Output,
                                    0);
                                IPin pinInAudioExt4 = DsFindPin.ByDirection((IBaseFilter) _AudioExtFilter,
                                    PinDirection.Input, 0);
                                hr = graphBuilder.Connect(pinOutAudioExt3, pinInAudioExt4);

                                //Render outpin from Audio Decoder
                                DirectShowUtil.RenderUnconnectedOutputPins(graphBuilder, _AudioExtFilter);

                                //Cleanup External Audio (Release)
                                if (_AudioSourceFilter != null)
                                {
                                    DirectShowUtil.ReleaseComObject(_AudioSourceFilter);
                                    _AudioSourceFilter = null;
                                }
                                if (_AudioExtSplitterFilter != null)
                                {
                                    DirectShowUtil.ReleaseComObject(_AudioExtSplitterFilter);
                                    _AudioExtSplitterFilter = null;
                                }
                                if (_AudioExtFilter != null)
                                {
                                    DirectShowUtil.ReleaseComObject(_AudioExtFilter);
                                    _AudioExtFilter = null;
                                }
                                if (pinOutAudioExt1 != null)
                                {
                                    DirectShowUtil.ReleaseComObject(pinOutAudioExt1);
                                    pinOutAudioExt1 = null;
                                }
                                if (pinInAudioExt2 != null)
                                {
                                    DirectShowUtil.ReleaseComObject(pinInAudioExt2);
                                    pinInAudioExt2 = null;
                                }
                                if (pinOutAudioExt3 != null)
                                {
                                    DirectShowUtil.ReleaseComObject(pinOutAudioExt3);
                                    pinOutAudioExt3 = null;
                                }
                                if (pinInAudioExt4 != null)
                                {
                                    DirectShowUtil.ReleaseComObject(pinInAudioExt4);
                                    pinInAudioExt4 = null;
                                }

                                Log.Debug("VideoPlayerVMR9 : External audio file loaded \"{0}\"", file);
                                AudioExternal = true;
                                break;
                        }
                    }
                }

                #endregion

                // Add preferred audio filters
                UpdateFilters("Audio");

                #region Set High Audio

                //Set High Resolution Output > 2 channels
                IBaseFilter baseFilter = null;
                bool FFDShowLoaded = false;
                graphBuilder.FindFilterByName("WMAudio Decoder DMO", out baseFilter);
                if (baseFilter != null && filterConfig.wmvAudio != false) //Also check configuration option enabled
                {
                    //Set the filter setting to enable more than 2 audio channels
                    const string g_wszWMACHiResOutput = "_HIRESOUTPUT";
                    object val = true;
                    IPropertyBag propBag = (IPropertyBag) baseFilter;
                    hr = propBag.Write(g_wszWMACHiResOutput, ref val);
                    if (hr != 0)
                    {
                        Log.Info("VideoPlayer9: Unable to turn WMAudio multichannel on. Reason: {0}", hr);
                    }
                    else
                    {
                        Log.Info("VideoPlayer9: WMAudio Decoder now set for > 2 audio channels");
                    }
                    if (!FFDShowLoaded)
                    {
                        IBaseFilter FFDShowAudio = DirectShowUtil.GetFilterByName(graphBuilder,
                            FFDSHOW_AUDIO_DECODER_FILTER);
                        if (FFDShowAudio != null)
                        {
                            DirectShowUtil.ReleaseComObject(FFDShowAudio);
                            FFDShowAudio = null;
                        }
                        else
                        {
                            _FFDShowAudio = DirectShowUtil.AddFilterToGraph(graphBuilder, FFDSHOW_AUDIO_DECODER_FILTER);
                        }
                        FFDShowLoaded = true;
                    }
                    DirectShowUtil.ReleaseComObject(baseFilter);
                    baseFilter = null;
                }

                #endregion

                if (_interfaceSourceFilter != null)
                {
                    DirectShowUtil.RenderGraphBuilderOutputPins(graphBuilder, _interfaceSourceFilter);
                }

                //Test and remove orphelin Audio Renderer
                //RemoveAudioR();

                //remove InternalScriptRenderer as it takes subtitle pin
                disableISR();

                //disable Closed Captions!
                disableCC();

                DirectShowUtil.RemoveUnusedFiltersFromGraph(graphBuilder);

                //remove orphelin audio renderer
                RemoveAudioR();

                //EnableClock();

                if ((Vmr9 == null || !Vmr9.IsVMR9Connected) && !AudioOnly)
                {
                    Log.Error("VideoPlayer9: Failed to render file -> vmr9");
                    mediaCtrl = null;
                    Cleanup();
                    return false;
                }

                mediaCtrl = (IMediaControl) graphBuilder;
                mediaEvt = (IMediaEventEx) graphBuilder;
                mediaSeek = (IMediaSeeking) graphBuilder;
                mediaPos = (IMediaPosition) graphBuilder;
                basicAudio = (IBasicAudio) graphBuilder;
                videoWin = (IVideoWindow) graphBuilder;
                if (Vmr9 != null)
                {
                    m_iVideoWidth = Vmr9.VideoWidth;
                    m_iVideoHeight = Vmr9.VideoHeight;
                    Vmr9.SetDeinterlaceMode();
                }
                return true;
            }
            catch (Exception ex)
            {
                Error.SetError("Unable to play movie", "Unable build graph for VMR9");
                Log.Error("VideoPlayer9: Exception while creating DShow graph {0} {1}", ex.Message, ex.StackTrace);
                Cleanup();
                return false;
            }
        }

        public void InvokePrivateAddFilterToGraphAndRelease(string audio)
        {
            MethodInfo dynMethod = this.GetType()
                .GetMethod("AddFilterToGraphAndRelease", BindingFlags.NonPublic | BindingFlags.Instance);
            dynMethod.Invoke(this, new object[] {audio});
        }

    }

    public class PlayerFactory : IPlayerFactory
    {
        private MediaPortal.Player.PlayerFactory _original;

        public PlayerFactory()
        {
            _original = new MediaPortal.Player.PlayerFactory();
        }
        public static PlayerFactory Instance = new PlayerFactory();

        public IExternalPlayer GetExternalPlayer(string fileName)
        {
            Log.Info("Askig for external player");
            return _original.GetExternalPlayer(fileName);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IPlayer Create(string fileName)
        {
            Log.Info("Using Anime Player Factory");
            Uri uri = new Uri(fileName);

            if (uri.Scheme.StartsWith("rtmp") || uri.Scheme.StartsWith("http") || uri.Scheme == "sop" ||
                uri.Scheme == "mms")
            {
                Log.Info("Using Anime Player");

                return new Player();
            }
            return _original.Create(fileName);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IPlayer Create(string fileName, g_Player.MediaType type)
        {
            Log.Info("Using Anime Player Factory");
            Uri uri = new Uri(fileName);

            if (uri.Scheme.StartsWith("rtmp") || uri.Scheme.StartsWith("http") || uri.Scheme == "sop" ||
                uri.Scheme == "mms")
            {
                Log.Info("Using Anime Player");

                return new Player();
            }
            return _original.Create(fileName, type);
        }
    }
}