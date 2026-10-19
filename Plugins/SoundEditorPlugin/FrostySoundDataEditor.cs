using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Frosty.Core;
using FrostySdk.Managers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WaveFormRendererLib;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
using WaveFormatExtensible = SharpDX.Multimedia.WaveFormatExtensible;

namespace SoundEditorPlugin
{
    /// <summary>
    /// The plugin's native EA Layer 3 decoder.
    ///
    /// The decoder is looked up where it is deployed (see <see cref="NativeAudioTools"/>) and loaded
    /// before the first call, rather than being asked for as "../thirdparty/ealayer3.dll": that path is
    /// resolved against the process's working directory, so a decoder that is sitting in the right
    /// folder was still not found whenever the editor was started from anywhere else - and the sound
    /// editor then had no audio for any chunk that needed it. The declaration below names the library
    /// without a folder, which is what the runtime binds to the module loaded here.
    /// </summary>
    public static class EALayer3
    {
        public delegate void AudioCallback([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] short[] data, int count, StreamInfo info);

        [StructLayout(LayoutKind.Sequential)]
        public struct StreamInfo
        {
            public int streamIndex;
            public int numChannels;
            public int sampleRate;
        }

        /// <summary>The native decoder this plugin uses.</summary>
        public const string NativeFileName = "ealayer3.dll";

        private static bool loadAttempted;

        /// <summary>
        /// Serialises the load. An editor decodes several clips at once (the track list is built on a task, and a
        /// waveform is rendered per clip), and a second thread that arrived while the first was still inside
        /// `LoadLibrary` used to see `loadAttempted` set, skip the wait, and bind `DllImport("ealayer3.dll")` by
        /// name before the module existed - which fails with "Unable to load DLL 'ealayer3.dll': the specified
        /// module could not be found" even though the decoder is deployed. Measured 2026-10-17 on the census run
        /// over the exported corpus: every decode failed at eight threads, and the same clip decoded fine alone.
        /// </summary>
        private static readonly object loadGate = new object();

        /// <summary>The path the decoder was loaded from, or null when it is not usable.</summary>
        public static string LoadedFrom { get; private set; }

        /// <summary>Why the decoder is not usable, or null while it is.</summary>
        public static string UnavailableReason { get; private set; }

        /// <summary>Why the last <see cref="TryDecode"/> returned nothing.</summary>
        public static string LastFailure { get; private set; }

        public static bool IsAvailable
        {
            get { EnsureLoaded(); return LoadedFrom != null; }
        }

        /// <summary>
        /// Loads the decoder once, and says so in the log when it cannot be used at all. A caller that arrives while
        /// the load is in progress waits for it rather than binding the library by name before it exists.
        /// </summary>
        public static void EnsureLoaded()
        {
            lock (loadGate)
            {
                if (loadAttempted)
                    return;

                string path = NativeAudioTools.Find(NativeFileName);
                if (path == null)
                {
                    loadAttempted = true;
                    UnavailableReason = NativeAudioTools.NotFoundMessage(NativeFileName);
                    NativeAudioTools.LogWarning("{0} EA Layer 3 chunks cannot be decoded until it is deployed.", UnavailableReason);
                    return;
                }

                if (NativeAudioTools.Preload(path))
                {
                    LoadedFrom = path;
                    loadAttempted = true;
                    return;
                }

                loadAttempted = true;
                UnavailableReason = "the EA Layer 3 decoder at " + path + " could not be loaded";
                NativeAudioTools.LogWarning("{0} EA Layer 3 chunks cannot be decoded.", UnavailableReason);
            }
        }

        /// <summary>
        /// Decodes one EA Layer 3 buffer, or returns null having recorded why it could not. The native
        /// decoder faults outright on streams it cannot take, and that exception used to travel out of
        /// InitialLoad and leave the whole editor without a track list, so nothing escapes from here.
        /// </summary>
        public static short[] TryDecode(byte[] buffer)
        {
            EnsureLoaded();

            if (LoadedFrom == null || buffer == null || buffer.Length == 0)
                return null;

            try
            {
                List<short> decoded = new List<short>();
                Decode(buffer, buffer.Length, (short[] data, int count, StreamInfo info) =>
                {
                    if (info.streamIndex == -1)
                        return;
                    decoded.AddRange(data);
                });

                if (decoded.Count == 0)
                {
                    LastFailure = "the EA Layer 3 decoder returned no samples";
                    return null;
                }

                return decoded.ToArray();
            }
            catch (Exception ex)
            {
                LastFailure = ex.Message;
                return null;
            }
        }

        /// <summary>A sentence naming why an EA Layer 3 chunk has no audio, for a per track warning.</summary>
        public static string DescribeFailure()
        {
            EnsureLoaded();

            if (UnavailableReason != null)
                return UnavailableReason;
            if (LastFailure != null)
                return "the EA Layer 3 decoder could not read this stream (" + LastFailure + ")";
            return "the EA Layer 3 decoder could not read this stream";
        }

        [DllImport(NativeFileName, EntryPoint = "Decode")]
        public static extern void Decode([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] buffer, int length, AudioCallback callback);
    }

    public static class Pcm16b
    {
        public static short[] Decode(byte[] soundBuffer)
        {
            using (NativeReader reader = new NativeReader(new MemoryStream(soundBuffer)))
            {
                ushort blockType = reader.ReadUShort();
                ushort blockSize = reader.ReadUShort(Endian.Big);
                byte compressionType = reader.ReadByte();

                int channelCount = (reader.ReadByte() >> 2) + 1;
                ushort sampleRate = reader.ReadUShort(Endian.Big);
                int totalSampleCount = reader.ReadInt(Endian.Big) & 0x00ffffff;

                List<short>[] channels = new List<short>[channelCount];
                for (int i = 0; i < channelCount; i++)
                    channels[i] = new List<short>();

                while (reader.Position <= reader.Length)
                {
                    blockType = reader.ReadUShort();
                    blockSize = reader.ReadUShort(Endian.Big);

                    if (blockType == 0x45)
                        break;

                    uint samples = reader.ReadUInt(Endian.Big);

                    for (int i = 0; i < samples; i++)
                    {
                        for (int j = 0; j < channelCount; j++)
                            channels[j].Add(reader.ReadShort(Endian.Big));
                    }
                }

                short[] outBuffer = new short[channels[0].Count * channelCount];
                for (int i = 0; i < channels[0].Count; i++)
                {
                    for (int j = 0; j < channelCount; j++)
                    {
                        outBuffer[(i * channelCount) + j] = channels[j][i];
                    }
                }

                return outBuffer;
            }
        }
    }
    public static class XAS
    {
        public static short[] Decode(byte[] soundBuffer)
        {
            using (NativeReader reader = new NativeReader(new MemoryStream(soundBuffer)))
            {
                ushort blockType = reader.ReadUShort();
                ushort blockSize = reader.ReadUShort(Endian.Big);
                byte compressionType = reader.ReadByte();

                int channelCount = (reader.ReadByte() >> 2) + 1;
                ushort sampleRate = reader.ReadUShort(Endian.Big);
                int totalSampleCount = reader.ReadInt(Endian.Big) & 0x00ffffff;

                List<short>[] channels = new List<short>[channelCount];
                for (int i = 0; i < channelCount; i++)
                    channels[i] = new List<short>();

                while (reader.Position <= reader.Length)
                {
                    blockType = reader.ReadUShort();
                    blockSize = reader.ReadUShort(Endian.Big);

                    if (blockType == 0x45)
                        break;

                    uint samples = reader.ReadUInt(Endian.Big);

                    byte[] buffer = null;
                    short[] blockBuffer = new short[32];
                    int[] consts1 = new int[4] { 0, 240, 460, 392 };
                    int[] consts2 = new int[4] { 0, 0, -208, -220 };

                    for (int i = 0; i < (blockSize / 76 / channelCount); i++)
                    {
                        for (int j = 0; j < channelCount; j++)
                        {
                            buffer = reader.ReadBytes(76);

                            for (int k = 0; k < 4; k++)
                            {
                                blockBuffer[0] = (short)(buffer[k * 4 + 0] & 0xF0 | buffer[k * 4 + 1] << 8);
                                blockBuffer[1] = (short)(buffer[k * 4 + 2] & 0xF0 | buffer[k * 4 + 3] << 8);

                                int index4 = (int)buffer[k * 4] & 0x0F;
                                int num10 = (int)buffer[k * 4 + 2] & 0x0F;
                                int index5 = 2;

                                while (index5 < 32)
                                {
                                    int num11 = ((int)buffer[12 + k + index5 * 2] & 240) >> 4;
                                    if (num11 > 7)
                                        num11 -= 16;

                                    int num12 = blockBuffer[index5 - 1] * consts1[index4] + blockBuffer[index5 - 2] * consts2[index4];

                                    blockBuffer[index5] = (short)(num12 + (num11 << 20 - num10) + 128 >> 8);
                                    if (blockBuffer[index5] > (int)short.MaxValue)
                                        blockBuffer[index5] = (int)short.MaxValue;
                                    else if (blockBuffer[index5] < (int)short.MinValue)
                                        blockBuffer[index5] = (int)short.MinValue;

                                    int num13 = (int)buffer[12 + k + index5 * 2] & 15;
                                    if (num13 > 7)
                                        num13 -= 16;

                                    int num14 = blockBuffer[index5] * consts1[index4] + blockBuffer[index5 - 1] * consts2[index4];

                                    blockBuffer[index5 + 1] = (short)(num14 + (num13 << 20 - num10) + 128 >> 8);
                                    if (blockBuffer[index5 + 1] > (int)short.MaxValue)
                                        blockBuffer[index5 + 1] = (int)short.MaxValue;
                                    else if (blockBuffer[index5 + 1] < (int)short.MinValue)
                                        blockBuffer[index5 + 1] = (int)short.MinValue;

                                    index5 += 2;
                                }

                                channels[j].AddRange(blockBuffer);
                            }

                            uint sampleSize = (samples < 128) ? samples : 128;
                            samples -= sampleSize;
                        }
                    }
                }

                short[] outBuffer = new short[channels[0].Count * channelCount];
                for (int i = 0; i < channels[0].Count; i++)
                {
                    for (int j = 0; j < channelCount; j++)
                    {
                        outBuffer[(i * channelCount) + j] = channels[j][i];
                    }
                }

                return outBuffer;
            }
        }
    }

    public class SoundDataTrack : INotifyPropertyChanged
    {
        /// <summary>
        /// The track's place in the asset, counting from 1, as the list shows it. It is a property
        /// rather than the list's own row number because the list is filtered while the user types and
        /// a filter that renumbered the clips would stop the numbers matching anything outside the list.
        /// </summary>
        public int Index { get; set; }

        public string Codec { get; set; }

        /// <summary>
        /// Which row of its key's block this clip is, as `1 of 2`, when a bank files more than one clip under one
        /// selection key. FUN_147986f40 turns a key into a block of Selection rows (a start and a count), so clips
        /// that share a key are told apart by their place in that block, and that place is what this column says.
        /// Empty when the clip is the only one its key finds, and for an asset that is not a bank.
        /// </summary>
        public string Variation { get; set; }
        public double Duration { get; set; }
        public int SegmentCount { get; set; }
        public string Language { get; set; }
        public ImageSource WaveForm { get; set; }
        public int SampleRate { get; set; }
        public int ChannelCount { get; set; }
        public short[] Samples { get; set; }
        public uint LoopStart { get; set; }
        public uint LoopEnd { get; set; }

        /// <summary>
        /// The key/value detail of this clip: its place, its codec and the bank keys it answers to. It
        /// belongs beside the name because a name alone cannot be checked - the keys can.
        /// </summary>
        public string Detail
        {
            get => detail;
            set { detail = value; NotifyPropertyChanged(); }
        }
        private string detail;

        /// <summary>The clip's byte range inside its chunk, which is searchable so a clip can be pointed at in the data.</summary>
        public string ClipText { get; set; }

        /// <summary>
        /// The bank key this clip answers to, as the bank states it: <c>sfx_team_id=241</c>. It is the clip's
        /// own key, not its bank's pool, and it is what makes a row checkable - a name can be wrong, the key
        /// the file states cannot.
        /// </summary>
        public string KeyText { get; set; }

        /// <summary>
        /// True once the track's audio is in <see cref="Samples"/>. A bank with thousands of clips
        /// decodes the ones it is asked for rather than all of them at open, and this is what says
        /// whether that has happened for this track.
        /// </summary>
        public bool SamplesLoaded { get; set; }

        /// <summary>
        /// The name the user sees. Settable: renaming a track is a label for the session and for the
        /// files an export writes, and it is the only field of a track the user edits directly.
        /// </summary>
        public string Name
        {
            get => name;
            set { name = value; NotifyPropertyChanged(); }
        }
        private string name;

        public double Progress { get => progress; set { progress = value; NotifyPropertyChanged(); } }
        private double progress;

        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SoundWave : IDisposable
    {
        public SoundDataTrack track;
        public event RoutedEventHandler OnFinishedPlaying;
        public double Progress => (voice.State.SamplesPlayed - (loopPtr / track.ChannelCount) < 0) ? voice.State.SamplesPlayed / (double)(SampleCount / track.ChannelCount) : (voice.State.SamplesPlayed - (loopPtr / track.ChannelCount)) / (double)(SampleCount / track.ChannelCount);
        public long SampleCount => track.Samples.Length;

        private SourceVoice voice;
        private AudioBuffer buffer;
        private int bufferPtr;
        private long loopPtr;
        private long loopCount;

        public SoundWave(SoundDataTrack inTrack, AudioPlayer player)
        {
            track = inTrack;

            WaveFormatExtensible format = new WaveFormatExtensible(track.SampleRate, 16, track.ChannelCount);
            switch (track.ChannelCount)
            {
                case 2: format.ChannelMask = Speakers.FrontLeft | Speakers.FrontRight; break;
                case 4: format.ChannelMask = Speakers.FrontLeft | Speakers.FrontRight | Speakers.BackLeft | Speakers.BackRight; break;
                case 6: format.ChannelMask = Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.LowFrequency | Speakers.BackLeft | Speakers.BackRight; break;
                default: format.ChannelMask = 0; break;
            }

            voice = new SourceVoice(player.AudioSystem, format, true);
            voice.SetOutputVoices(new VoiceSendDescriptor(player.OutputVoice));
            voice.BufferEnd += Voice_BufferEnd;

            Voice_BufferEnd(IntPtr.Zero);

            //voice.SetVolume(Math.Min(Config.Get("SoundVolume", 20.0f) / 100.0f, 1.0f));
            //voice.SetVolume((float)Math.Min(Config.Get<float>("Editor", "SoundVolume", 20) / 100, 1.0));
            voice.Start();
        }

        private const int MAX_BUFFER_SIZE = 4096;
        private void Voice_BufferEnd(IntPtr obj)
        {
            if (bufferPtr < SampleCount)
            {
                int bufferSize = (SampleCount - bufferPtr > MAX_BUFFER_SIZE * track.ChannelCount) ? MAX_BUFFER_SIZE * track.ChannelCount : (int)(SampleCount - bufferPtr);
                DataStream DS = new DataStream(bufferSize * sizeof(short), true, true);
                buffer = new AudioBuffer
                {
                    Stream = DS,
                    AudioBytes = (int)DS.Length,
                    Flags = BufferFlags.None
                };

                // interleave channels
                while (DS.Position < DS.Length)
                {
                    DS.Write(track.Samples[bufferPtr]);
                    bufferPtr++;

                    if (track.LoopEnd != 0 && bufferPtr == track.LoopEnd)
                    {
                        loopPtr += bufferPtr - track.LoopStart;
                        loopCount++;

                        bufferPtr = (int)track.LoopStart;
                    }
                }

                voice.SubmitSourceBuffer(buffer, null);
            }
            else
            {
                OnFinishedPlaying?.Invoke(this, new RoutedEventArgs());
            }
        }

        public void Dispose()
        {
            voice.Stop();
            voice.DestroyVoice();
            voice.Dispose();
        }
    }

    public class AudioPlayer : IDisposable
    {
        public XAudio2 AudioSystem { get; }
        public MasteringVoice OutputVoice { get; }
        public double Progress => currentSound?.Progress ?? 0.0;
        public bool IsPlaying { get; private set; }

        private SoundWave currentSound;

        public AudioPlayer()
        {
            AudioSystem = new XAudio2();
            OutputVoice = new MasteringVoice(AudioSystem, 8);
        }

        public void PlaySound(SoundDataTrack track)
        {
            SoundDispose();

            currentSound = new SoundWave(track, this);
            IsPlaying = true;
            currentSound.OnFinishedPlaying += CurrentSound_OnFinishedPlaying;
        }

        private void CurrentSound_OnFinishedPlaying(object sender, RoutedEventArgs e)
        {
            IsPlaying = false;
            //SoundDispose();
        }

        public void SoundDispose()
        {
            IsPlaying = false;

            if (currentSound == null)
                return;

            var tmpSound = currentSound;
            currentSound = null;
            tmpSound.Dispose();
        }

        public void Dispose()
        {
            SoundDispose();

            OutputVoice.Dispose();
            AudioSystem.Dispose();
        }
    }

    [TemplatePart(Name = PART_TracksListBox, Type = typeof(ListView))]
    [TemplatePart(Name = PART_PlayButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_StopButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_VolumeSlider, Type = typeof(Slider))]
    [TemplatePart(Name = PART_AddClipButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_DeleteClipButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_ImportButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_ExportButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_EditMenuButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_SoundExportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundExportAllMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundImportMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundAddTrackMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundDeleteTrackMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundDeleteAllExceptMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditNormalizeMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditGainMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditFadeInMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditFadeOutMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditTrimSilenceMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditTrimRangeMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditReverseMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundEditUndoMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SearchBox, Type = typeof(TextBox))]
    [TemplatePart(Name = PART_TrackCountText, Type = typeof(TextBlock))]
    [TemplatePart(Name = PART_BankSummary, Type = typeof(TextBlock))]
    [TemplatePart(Name = PART_DetailText, Type = typeof(TextBlock))]
    [TemplatePart(Name = PART_PrevTrackButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_NextTrackButton, Type = typeof(Button))]
    [TemplatePart(Name = PART_WaveFormCanvas, Type = typeof(Canvas))]
    [TemplatePart(Name = PART_WaveFormImage, Type = typeof(Image))]
    [TemplatePart(Name = PART_ProgressLine, Type = typeof(System.Windows.Shapes.Line))]
    [TemplatePart(Name = PART_SoundCopyNameMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundCopyKeysMenuItem, Type = typeof(MenuItem))]
    [TemplatePart(Name = PART_SoundRenameMenuItem, Type = typeof(MenuItem))]
    public class FrostySoundDataEditor : FrostyAssetEditor
    {
        private const string PART_TracksListBox = "PART_TracksListBox";
        private const string PART_PlayButton = "PART_PlayButton";
        private const string PART_StopButton = "PART_StopButton";
        private const string PART_VolumeSlider = "PART_VolumeSlider";
        private const string PART_AddClipButton = "PART_AddClipButton";
        private const string PART_DeleteClipButton = "PART_DeleteClipButton";
        private const string PART_ImportButton = "PART_ImportButton";
        private const string PART_ExportButton = "PART_ExportButton";
        private const string PART_EditMenuButton = "PART_EditMenuButton";
        private const string PART_SoundExportMenuItem = "PART_SoundExportMenuItem";
        private const string PART_SoundExportAllMenuItem = "PART_SoundExportAllMenuItem";
        private const string PART_SoundImportMenuItem = "PART_SoundImportMenuItem";
        private const string PART_SoundAddTrackMenuItem = "PART_SoundAddTrackMenuItem";
        private const string PART_SoundDeleteTrackMenuItem = "PART_SoundDeleteTrackMenuItem";
        private const string PART_SoundDeleteAllExceptMenuItem = "PART_SoundDeleteAllExceptMenuItem";
        private const string PART_SoundEditNormalizeMenuItem = "PART_SoundEditNormalizeMenuItem";
        private const string PART_SoundEditGainMenuItem = "PART_SoundEditGainMenuItem";
        private const string PART_SoundEditFadeInMenuItem = "PART_SoundEditFadeInMenuItem";
        private const string PART_SoundEditFadeOutMenuItem = "PART_SoundEditFadeOutMenuItem";
        private const string PART_SoundEditTrimSilenceMenuItem = "PART_SoundEditTrimSilenceMenuItem";
        private const string PART_SoundEditTrimRangeMenuItem = "PART_SoundEditTrimRangeMenuItem";
        private const string PART_SoundEditReverseMenuItem = "PART_SoundEditReverseMenuItem";
        private const string PART_SoundEditUndoMenuItem = "PART_SoundEditUndoMenuItem";
        private const string PART_SearchBox = "PART_SearchBox";
        private const string PART_TrackCountText = "PART_TrackCountText";
        private const string PART_BankSummary = "PART_BankSummary";
        private const string PART_DetailText = "PART_DetailText";
        private const string PART_PrevTrackButton = "PART_PrevTrackButton";
        private const string PART_NextTrackButton = "PART_NextTrackButton";
        private const string PART_SoundCopyNameMenuItem = "PART_SoundCopyNameMenuItem";
        private const string PART_SoundCopyKeysMenuItem = "PART_SoundCopyKeysMenuItem";
        private const string PART_SoundRenameMenuItem = "PART_SoundRenameMenuItem";
        private const string PART_WaveFormCanvas = "PART_WaveFormCanvas";
        private const string PART_WaveFormImage = "PART_WaveFormImage";
        private const string PART_ProgressLine = "PART_ProgressLine";

        public static readonly DependencyProperty TracksListProperty = DependencyProperty.Register("TracksList", typeof(ObservableCollection<SoundDataTrack>), typeof(FrostySoundDataEditor), new FrameworkPropertyMetadata(null));
        public ObservableCollection<SoundDataTrack> TracksList
        {
            get => (ObservableCollection<SoundDataTrack>)GetValue(TracksListProperty);
            set => SetValue(TracksListProperty, value);
        }

        public bool IsPlaying => audioPlayer != null && audioPlayer.IsPlaying;

        protected ListView tracksListBox;
        private Button playButton;
        private Button stopButton;
        private Button prevTrackButton;
        private Button nextTrackButton;
        private Slider volumeSlider;
        private MenuItem exportAllMenuItem;
        private Button addClipButton;
        private Button deleteClipButton;
        private MenuItem addClipMenuItem;
        private MenuItem deleteClipMenuItem;
        private Button importButton;
        private Button exportButton;
        private Button editMenuButton;
        private TextBox searchBox;
        private TextBlock trackCountText;
        private TextBlock bankSummaryText;
        private TextBlock detailText;

        /// <summary>
        /// One waveform, for the clip the user selected. A bank can hold thousands of clips behind a shared
        /// chunk, and a waveform rendered inside every row is thousands of 800-pixel images: with the rows
        /// virtualized a row's own image is not even the reason it is slow, since the image is laid out for
        /// every row that becomes visible and never reused. One image, drawn on selection, is the whole of
        /// what a clip list needs.
        /// </summary>
        private Canvas waveFormCanvas;
        private Image waveFormImage;
        private System.Windows.Shapes.Line progressLine;

        protected AudioPlayer audioPlayer;
        private bool bFirstTime = true;
        private string filterText;

        /// <summary>What each track's samples were before the edits that were written to the asset.</summary>
        protected readonly TrackEditHistory EditHistory = new TrackEditHistory();

        /// <summary>
        /// Set by a write-back that moved bytes belonging to tracks other than the one edited. Those
        /// tracks' cached samples and offsets still describe where their audio used to be, so the list
        /// is rebuilt from the asset in that case instead of being left to look right.
        /// </summary>
        protected bool TracksNeedReload { get; private set; }

        protected void RequireTrackReload()
        {
            TracksNeedReload = true;
        }

        public FrostySoundDataEditor(ILogger inLogger) 
            : base(inLogger)
        {
        }

        static FrostySoundDataEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostySoundDataEditor), new FrameworkPropertyMetadata(typeof(FrostySoundDataEditor)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            tracksListBox = GetTemplateChild(PART_TracksListBox) as ListView;

            playButton = GetTemplateChild(PART_PlayButton) as Button;
            playButton.Click += PlayButton_Click;

            stopButton = GetTemplateChild(PART_StopButton) as Button;
            stopButton.Click += StopButton_Click;

            volumeSlider = GetTemplateChild(PART_VolumeSlider) as Slider;

            volumeSlider.Value = Math.Min(Config.Get<float>("SoundVolume", 20.0f), 100);
            //volumeSlider.Value = (float)Math.Min(Config.Get<float>("Editor", "SoundVolume", 20), 100);

            volumeSlider.ValueChanged += VolumeSlider_ValueChanged;

            tracksListBox.SelectionChanged += TracksListBox_SelectionChanged;
            MenuItem mi = GetTemplateChild(PART_SoundExportMenuItem) as MenuItem;
            mi.Click += SoundExportMenuItem_Click;
            mi = GetTemplateChild(PART_SoundExportAllMenuItem) as MenuItem;
            if (mi != null)
            {
                mi.Click += SoundExportAllMenuItem_Click;
                exportAllMenuItem = mi;
            }
            mi = GetTemplateChild(PART_SoundImportMenuItem) as MenuItem;
            mi.Click += SoundImportMenuItem_Click;
            mi = GetTemplateChild(PART_SoundAddTrackMenuItem) as MenuItem;
            if (mi != null)
            {
                mi.Click += SoundAddTrackMenuItem_Click;
                addClipMenuItem = mi;
            }
            mi = GetTemplateChild(PART_SoundDeleteTrackMenuItem) as MenuItem;
            if (mi != null)
            {
                mi.Click += SoundDeleteTrackMenuItem_Click;
                deleteClipMenuItem = mi;
            }
            mi = GetTemplateChild(PART_SoundDeleteAllExceptMenuItem) as MenuItem;
            if (mi != null) mi.Click += SoundDeleteAllExceptMenuItem_Click;

            WireEditMenuItem(PART_SoundEditUndoMenuItem, SoundEditUndoMenuItem_Click);
            WireEditMenuItem(PART_SoundEditNormalizeMenuItem, SoundEditNormalizeMenuItem_Click);
            WireEditMenuItem(PART_SoundEditGainMenuItem, SoundEditGainMenuItem_Click);
            WireEditMenuItem(PART_SoundEditFadeInMenuItem, SoundEditFadeInMenuItem_Click);
            WireEditMenuItem(PART_SoundEditFadeOutMenuItem, SoundEditFadeOutMenuItem_Click);
            WireEditMenuItem(PART_SoundEditTrimSilenceMenuItem, SoundEditTrimSilenceMenuItem_Click);
            WireEditMenuItem(PART_SoundEditTrimRangeMenuItem, SoundEditTrimRangeMenuItem_Click);
            WireEditMenuItem(PART_SoundEditReverseMenuItem, SoundEditReverseMenuItem_Click);

            Loaded += FrostySoundDataEditor_Loaded;

            TracksList = new ObservableCollection<SoundDataTrack>();
            TracksList.CollectionChanged += TracksListChanged;

            WireListChrome();
            WireToolbar();

            waveFormCanvas = GetTemplateChild(PART_WaveFormCanvas) as Canvas;
            waveFormImage = GetTemplateChild(PART_WaveFormImage) as Image;
            progressLine = GetTemplateChild(PART_ProgressLine) as System.Windows.Shapes.Line;

            if (waveFormCanvas != null)
                waveFormCanvas.SizeChanged += (s, e) => SizeWaveForm();

            // A subclass that contributes its own "export every track" command keeps that one command,
            // so the menu never offers two ways to write the same folder. The decision needs the loaded
            // tracks, which is why it is taken when the menu opens rather than when the template is built.
            if (tracksListBox.ContextMenu != null)
            {
                tracksListBox.ContextMenu.Opened += (s, e) =>
                {
                    if (exportAllMenuItem != null)
                        exportAllMenuItem.Visibility = HasOwnExportAllCommand ? Visibility.Collapsed : Visibility.Visible;
                };
            }

        }

        /// <summary>
        /// What a change to the list means: a rebuilt list can hold a different number of clips, so an undo
        /// entry kept from the previous list would hand the wrong audio back to the wrong clip, and the count
        /// above the list is stale either way.
        /// </summary>
        private void TracksListChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                EditHistory.Clear();

            UpdateTrackCount();
        }

        /// <summary>
        /// Replaces the whole list in one move.
        ///
        /// Adding clip by clip to a bound list raises one event per row, and each of those re-counts and
        /// re-filters the view: a bank of seven thousand clips pays seven thousand of those before the tab
        /// is usable, which is what made a large bank look frozen. The replacement collection is filled
        /// while nothing is bound to it, so the list sees a single change and lays out the visible rows.
        /// </summary>
        protected void SetTrackList(IEnumerable<SoundDataTrack> tracks)
        {
            ObservableCollection<SoundDataTrack> replacement = new ObservableCollection<SoundDataTrack>();
            if (tracks != null)
            {
                foreach (SoundDataTrack track in tracks)
                    replacement.Add(track);
            }

            replacement.CollectionChanged += TracksListChanged;
            TracksList = replacement;

            // The list is new, so nothing is selected and every command's answer has changed: the buttons and the
            // menu entries are asked again here rather than left saying what they said about the list that is gone.
            // "Delete Clip" used to keep whatever it was told when the tab was built - greyed, with no selection -
            // until the user happened to click a row, which is how a command that works looked like one that does not.
            UpdateCommandState();
            UpdateTrackCount();
        }

        // ═══════════════════════════════════════════════════════════
        //  The list's chrome: filter, counts, summary, detail, navigation, naming
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Wires the parts that make a bank of thousands of clips workable: a filter that narrows the
        /// list while the user types, a line saying what the asset is, a line saying what the selected
        /// clip is, and the commands that carry a clip's name and keys out of the editor.
        /// </summary>
        private void WireListChrome()
        {
            searchBox = GetTemplateChild(PART_SearchBox) as TextBox;
            if (searchBox != null)
                searchBox.TextChanged += (s, e) => { filterText = searchBox.Text; ApplyFilter(); };

            trackCountText = GetTemplateChild(PART_TrackCountText) as TextBlock;
            bankSummaryText = GetTemplateChild(PART_BankSummary) as TextBlock;
            detailText = GetTemplateChild(PART_DetailText) as TextBlock;

            prevTrackButton = GetTemplateChild(PART_PrevTrackButton) as Button;
            if (prevTrackButton != null)
                prevTrackButton.Click += (s, e) => MoveSelection(-1);

            nextTrackButton = GetTemplateChild(PART_NextTrackButton) as Button;
            if (nextTrackButton != null)
                nextTrackButton.Click += (s, e) => MoveSelection(1);

            WireEditMenuItem(PART_SoundCopyNameMenuItem, SoundCopyNameMenuItem_Click);
            WireEditMenuItem(PART_SoundCopyKeysMenuItem, SoundCopyKeysMenuItem_Click);
            WireEditMenuItem(PART_SoundRenameMenuItem, SoundRenameMenuItem_Click);
        }

        /// <summary>
        /// The selected track's place in <see cref="TracksList"/>.
        ///
        /// The list box's own index is a position in the *view*, and the view is filtered while the user
        /// types, so it is not the track's place in the asset. Every edit and write-back addresses the
        /// track itself, which is what this returns; using the list's index here would write an edit into
        /// whichever clip happened to sit at that row of the filtered list.
        /// </summary>
        protected int SelectedTrackIndex
        {
            get
            {
                if (!(tracksListBox?.SelectedItem is SoundDataTrack track) || TracksList == null)
                    return -1;

                return TracksList.IndexOf(track);
            }
        }

        /// <summary>Narrows the list to the tracks whose name, detail, codec or clip range contains the text.</summary>
        private void ApplyFilter()
        {
            ICollectionView view = TracksList == null ? null : CollectionViewSource.GetDefaultView(TracksList);
            if (view != null)
            {
                view.Filter = string.IsNullOrWhiteSpace(filterText)
                    ? (Predicate<object>)null
                    : (item => Matches(item as SoundDataTrack, filterText));
            }

            UpdateTrackCount();
            UpdateDetailText();
        }

        private static bool Matches(SoundDataTrack track, string text)
        {
            if (track == null)
                return false;

            string needle = (text ?? "").Trim();
            if (needle.Length == 0)
                return true;

            return ContainsText(track.Name, needle) || ContainsText(track.Detail, needle)
                || ContainsText(track.ClipText, needle) || ContainsText(track.Codec, needle);
        }

        private static bool ContainsText(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>How many tracks the list is showing, against how many the asset holds.</summary>
        private void UpdateTrackCount()
        {
            if (trackCountText == null || TracksList == null)
                return;

            int total = TracksList.Count;
            int shown = CountVisible();

            trackCountText.Text = shown == total
                ? total.ToString("N0") + (total == 1 ? " track" : " tracks")
                : shown.ToString("N0") + " of " + total.ToString("N0") + " tracks";
        }

        private int CountVisible()
        {
            if (TracksList == null)
                return 0;

            ICollectionView view = CollectionViewSource.GetDefaultView(TracksList);
            if (view == null || view.Filter == null)
                return TracksList.Count;

            int shown = 0;
            foreach (object item in view)
            {
                if (item is SoundDataTrack)
                    shown++;
            }

            return shown;
        }

        /// <summary>
        /// What the summary above the list says. A bank of clips has more to say than its name - the
        /// path it lives under, its keys and the chunk it reads - so that is what a subclass supplies.
        /// </summary>
        protected virtual string BankSummary
        {
            get { return null; }
        }

        protected void RefreshBankSummary()
        {
            if (bankSummaryText == null)
                return;

            string summary = BankSummary;
            if (string.IsNullOrEmpty(summary))
            {
                int count = TracksList == null ? 0 : TracksList.Count;
                string codec = TracksList != null && TracksList.Count > 0 ? TracksList[0].Codec : null;

                summary = (AssetEntry?.Name ?? "sound asset")
                    + " · " + count.ToString("N0") + (count == 1 ? " track" : " tracks")
                    + (string.IsNullOrEmpty(codec) ? "" : " · " + codec);
            }

            bankSummaryText.Text = summary;
            bankSummaryText.ToolTip = summary;
        }

        /// <summary>
        /// The line a track that only knows what it is gets: where it sits, what it is encoded in, how
        /// long it is and how many channels it has. A loader that knows more - a bank clip has a player
        /// or a team behind it - fills its own line in instead.
        /// </summary>
        protected static string DescribeTrack(SoundDataTrack track, int number, int total)
        {
            StringBuilder text = new StringBuilder();
            text.Append("clip ").Append(number).Append(" of ").Append(total);

            if (!string.IsNullOrEmpty(track.Codec))
                text.Append(" · ").Append(track.Codec);

            if (track.SampleRate > 0)
                text.Append(" · ").Append(track.SampleRate.ToString("N0")).Append(" Hz");

            if (track.ChannelCount > 0)
                text.Append(" · ").Append(track.ChannelCount)
                    .Append(track.ChannelCount == 1 ? " channel" : " channels");

            if (track.Duration > 0)
                text.Append(" · ").Append(track.Duration.ToString("0.###")).Append(" s");

            if (track.SegmentCount > 1)
                text.Append(" · ").Append(track.SegmentCount).Append(" segments");

            // The localized wave path used to hang the language off the list as a column of its own; it
            // is part of what the clip is, so it belongs in the line that says what the clip is.
            if (!string.IsNullOrEmpty(track.Language))
                text.Append(" · ").Append(track.Language);

            return text.ToString();
        }

        /// <summary>
        /// Re-runs the filter, the count and the summary, for a subclass that has just changed a track
        /// or replaced the list.
        /// </summary>
        protected void RefreshTrackListChrome()
        {
            RefreshBankSummary();
            ApplyFilter();
        }

        /// <summary>One line about the selected clip: which clip it is and what it is called.</summary>
        private void UpdateDetailText()
        {
            if (detailText == null)
                return;

            if (!(tracksListBox?.SelectedItem is SoundDataTrack track))
            {
                detailText.Text = "";
                return;
            }

            StringBuilder text = new StringBuilder();

            if (track.Index > 0)
                text.Append("#").Append(track.Index).Append(" · ");

            text.Append(track.Name);

            if (!string.IsNullOrEmpty(track.Detail))
                text.Append(" · ").Append(track.Detail);

            if (!string.IsNullOrEmpty(track.Variation))
                text.Append(" \u00b7 variation ").Append(track.Variation);

            // Only what the clip is: where a name came from, what its byte offset is and how a research table read
            // its audio are properties of the tools, not of the clip this line is about.
            if (!track.SamplesLoaded)
                text.Append(" · audio not decoded yet");

            detailText.Text = text.ToString();
        }

        /// <summary>Moves the selection by one row of the list as it is currently filtered.</summary>
        private void MoveSelection(int delta)
        {
            if (tracksListBox == null || TracksList == null)
                return;

            ICollectionView view = CollectionViewSource.GetDefaultView(TracksList);
            if (view == null)
                return;

            List<object> items = new List<object>();
            foreach (object item in view)
                items.Add(item);

            if (items.Count == 0)
                return;

            int at = tracksListBox.SelectedItem == null ? -1 : items.IndexOf(tracksListBox.SelectedItem);
            int next = Math.Min(items.Count - 1, Math.Max(0, at + delta));

            tracksListBox.SelectedItem = items[next];
            tracksListBox.ScrollIntoView(items[next]);
        }

        private void SoundCopyNameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack track))
            {
                App.Logger.LogWarning("Select a track first.");
                return;
            }

            Clipboard.SetText(track.Name ?? "");
            App.Logger.Log("Copied the name of track #{0}: {1}", track.Index, track.Name);
        }

        private void SoundCopyKeysMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack track))
            {
                App.Logger.LogWarning("Select a track first.");
                return;
            }

            string text = string.IsNullOrEmpty(track.Detail) ? (track.Name ?? "") : track.Detail;
            Clipboard.SetText(text);
            App.Logger.Log("Copied the keys of track #{0}: {1}", track.Index, text);
        }

        /// <summary>
        /// Renames a track. The name is the editor's own label for the clip - it is what an export writes
        /// and what the search box looks at - and never a change to the asset, so a rename costs nothing
        /// and needs no reload.
        /// </summary>
        private void SoundRenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack track))
            {
                App.Logger.LogWarning("Select a track first.");
                return;
            }

            string name = PromptForText("Rename Track", "Name shown in the list and used for exported files:", track.Name);
            if (string.IsNullOrWhiteSpace(name))
                return;

            track.Name = name.Trim();

            ApplyFilter();
            UpdateDetailText();
            App.Logger.Log("Track #{0} renamed to {1}", track.Index, track.Name);
        }

        /// <summary>
        /// Makes sure a track's audio is in memory, decoding it when the asset stores clips inside a
        /// shared chunk rather than one chunk per track.
        ///
        /// Returns false when the audio cannot be decoded, with the reason already in the log, so a
        /// caller can stop instead of playing or editing silence. The base implementation has nothing to
        /// decode: a track built from its own chunk already carries its samples.
        /// </summary>
        protected bool EnsureSamples(int trackIndex)
        {
            if (trackIndex < 0 || TracksList == null || trackIndex >= TracksList.Count)
                return false;

            return EnsureSamples(TracksList[trackIndex], trackIndex);
        }

        /// <summary>
        /// The same, for a track the caller already holds; this is the one a subclass overrides. It is handed the
        /// track so that it never reads <see cref="TracksList"/>: that is a dependency property, and WPF throws when
        /// any thread but the UI thread reads one - which the export task, running on the task window's thread, did.
        /// </summary>
        protected virtual bool EnsureSamples(SoundDataTrack track, int trackIndex)
        {
            return track != null && track.Samples != null && track.Samples.Length > 0;
        }

        /// <summary>
        /// Decodes the selected track if it needs it, with a wait cursor while the decoder runs, and
        /// hands back the track and its index in the asset.
        /// </summary>
        private bool EnsureSelectedSamples(out int trackIndex, out SoundDataTrack track)
        {
            trackIndex = SelectedTrackIndex;
            track = trackIndex >= 0 ? TracksList[trackIndex] : null;

            if (track == null)
            {
                App.Logger.LogWarning("Select a track first.");
                return false;
            }

            if (EnsureSamples(trackIndex))
                return true;

            App.Logger.LogWarning("The audio of track #{0} could not be decoded, so it cannot be played or edited; see the warnings above.", track.Index);
            return false;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            audioPlayer.SoundDispose();
            stopButton.IsEnabled = false;

            if (tracksListBox.SelectedItem != null)
                playButton.IsEnabled = true;
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack currentTrack))
                return;

            // A bank clip has no samples until it is asked for, so playing decodes it first rather than
            // handing the audio layer a track whose samples are still in the chunk.
            if (!EnsureSamples(SelectedTrackIndex))
            {
                App.Logger.LogWarning("Track #{0} has no decodable audio, so there is nothing to play.", currentTrack.Index);
                return;
            }

            audioPlayer.OutputVoice.SetVolume((float)(volumeSlider.Value / 100.0));
            audioPlayer.PlaySound(currentTrack);

            playButton.IsEnabled = false;
            stopButton.IsEnabled = true;

            await Dispatcher.InvokeAsync(async () =>
            {
                while (IsPlaying)
                {
                    MoveProgressMarker();
                    await Task.Delay(30);
                }

                ResetProgressMarker();
                stopButton.IsEnabled = false;
                playButton.IsEnabled = true;
            });
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is Slider slider && slider.Value <= 100.0 && slider.Value >= 0.0)
            {
                audioPlayer.OutputVoice.SetVolume((float)(slider.Value / 100.0));

                //Config.Current["SoundVolume"] = slider.Value;
                Config.Add("SoundVolume", (float)slider.Value);
                Config.Save();
                //Config.Add("Editor", "SoundVolume", slider.Value);
            }
        }

        private void TracksListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (tracksListBox.SelectedItem == null)
                return;

            UpdateCommandState();

            if (!IsPlaying)
                playButton.IsEnabled = true;

            // Browsing a bank should show the clip you are looking at: its audio is decoded and its
            // waveform drawn when it is selected, not when the asset is opened. A bank can hold
            // thousands of clips behind one shared chunk, and decoding all of them to open a tab is
            // minutes of work for audio nobody has asked to hear.
            int index = SelectedTrackIndex;
            if (index >= 0 && tracksListBox.SelectedItem is SoundDataTrack track)
            {
                if (!track.SamplesLoaded)
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    try
                    {
                        EnsureSamples(index);
                    }
                    finally
                    {
                        Mouse.OverrideCursor = null;
                    }
                }

                if (track.WaveForm == null && track.Samples != null && track.Samples.Length > 0)
                    track.WaveForm = RenderWaveform(track.Samples);
            }

            ShowSelectedWaveForm();
            ResetProgressMarker();
            UpdateDetailText();
        }

        /// <summary>Lays the waveform and the playback marker out to the panel they are drawn in.</summary>
        private void SizeWaveForm()
        {
            if (waveFormCanvas == null)
                return;

            double width = waveFormCanvas.ActualWidth;
            double height = waveFormCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            if (waveFormImage != null)
            {
                waveFormImage.Width = width;
                waveFormImage.Height = height;
            }

            if (progressLine != null)
            {
                progressLine.Y1 = 0;
                progressLine.Y2 = height;
            }
        }

        /// <summary>
        /// Shows the selected clip's waveform. The bitmap is the one the clip already carries, rendered the
        /// first time the clip was selected or decoded, so browsing thousands of clips never draws the same
        /// one twice.
        /// </summary>
        private void ShowSelectedWaveForm()
        {
            if (waveFormImage == null)
                return;

            SoundDataTrack track = tracksListBox?.SelectedItem as SoundDataTrack;
            waveFormImage.Source = track?.WaveForm;
            SizeWaveForm();
        }

        /// <summary>Where playback has reached, as a line across the waveform above.</summary>
        private void MoveProgressMarker()
        {
            if (progressLine == null || waveFormCanvas == null || audioPlayer == null)
                return;

            double at = Math.Max(0.0, Math.Min(1.0, audioPlayer.Progress));
            double x = at * waveFormCanvas.ActualWidth;
            progressLine.X1 = x;
            progressLine.X2 = x;
        }

        private void ResetProgressMarker()
        {
            if (progressLine == null)
                return;

            progressLine.X1 = 0;
            progressLine.X2 = 0;
        }

        public override void Closed()
        {
            audioPlayer.Dispose();
        }

        private void FrostySoundDataEditor_Loaded(object sender, RoutedEventArgs e)
        {
            if (bFirstTime)
            {
                //volumeSlider.Value = Config.Get<float>("Editor", "SoundVolume", 20);
                audioPlayer = new AudioPlayer();

                // The native decoders are looked up once per tab. A missing one is a property of the
                // deployment, and reporting it here is what stops an asset whose audio cannot be decoded
                // from looking like a broken editor.
                EALayer3.EnsureLoaded();

                List<SoundDataTrack> tracks = null;
                RunTask("Loading Tracks", owner =>
                {
                    tracks = InitialLoad(owner);
                });

                if (tracks == null)
                {
                    // A load that threw used to leave exactly the same empty list as an asset that holds
                    // nothing, with no way to tell the two apart from the tab.
                    App.Logger.LogError("This sound asset could not be loaded, so no tracks are listed. " +
                        "See the warnings above for the chunk that failed.");
                    bFirstTime = false;
                    return;
                }

                // Every track is numbered and described here, not only the ones a richer loader built: the
                // list's columns are shared, and a track that arrives with no place and no line would show a
                // blank row in an otherwise informative list. A loader that knows more keeps what it set.
                int number = 0;
                foreach (SoundDataTrack track in tracks)
                {
                    number++;

                    if (track.Index <= 0)
                        track.Index = number;

                    if (string.IsNullOrEmpty(track.Name))
                        track.Name = "Clip " + number + " of " + tracks.Count;

                    if (string.IsNullOrEmpty(track.Detail))
                        track.Detail = DescribeTrack(track, track.Index, tracks.Count);
                }

                // The whole list is handed over at once: a bank of thousands of clips must not pay a
                // list-changed event, a re-count and a re-filter for every one of its rows.
                SetTrackList(tracks);

                RefreshBankSummary();
                ApplyFilter();

                if (tracks.Count == 0)
                {
                    App.Logger.LogWarning("This sound asset listed no tracks: the chunks it points at are either missing " +
                        "from the loaded game data or could not be decoded (see the warnings above).");
                }

                bFirstTime = false;
            }
        }

        protected virtual List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            return new List<SoundDataTrack>();
        }

        /// <summary>
        /// Runs <paramref name="work"/> under a task window and returns what it threw, or null.
        ///
        /// `FrostyTaskWindow` runs the work with `Task.Run` from its `async void` Loaded handler, so an exception
        /// thrown in the work never reaches a `try` around `Show`: the handler rethrows it on the UI thread, where
        /// nothing catches it and the editor goes down. That is how a cross-thread read in an edit took Frosty with
        /// it. The work is wrapped inside the callback instead, and what it threw is logged here. Nothing is reverted:
        /// what the work wrote before it stopped is the user's to keep or to revert, and the log says which edit it was.
        /// </summary>
        protected Exception RunTask(string title, Action<FrostyTaskWindow> work)
        {
            Exception failure = null;
            FrostyTaskWindow.Show(title, "", task =>
            {
                try
                {
                    work(task);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (failure != null)
            {
                App.Logger.LogError("{0} stopped on an error ({1}: {2}). Whatever it wrote before the error is kept; "
                    + "the asset's Revert command undoes all of its changes.", title, failure.GetType().Name, failure.Message);
                App.Logger.Log("{0}", failure.ToString());
            }

            return failure;
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread: at once when called there, through the dispatcher otherwise.</summary>
        protected void RunOnUiThread(Action action)
        {
            if (Dispatcher == null || Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.Invoke(action);
        }

        /// <summary>The asset's entry, read on the UI thread (`AssetEntry` is a dependency property).</summary>
        protected AssetEntry AssetEntryOnUiThread()
        {
            AssetEntry entry = null;
            RunOnUiThread(() => { entry = AssetEntry; });
            return entry;
        }

        /// <summary>The asset's name, read on the UI thread.</summary>
        protected string AssetNameOnUiThread()
        {
            AssetEntry entry = AssetEntryOnUiThread();
            return entry == null ? null : entry.Name;
        }

        /// <summary>
        /// Ties a chunk or a resource the edit wrote to this asset, on the UI thread. `AssetManager.RevertAsset`
        /// reverts an asset's linked entries with it, so a resource that is linked goes back together with the EBX
        /// and the chunk instead of being left describing audio that is no longer there.
        /// </summary>
        protected void LinkToAssetOnUiThread(IEnumerable<AssetEntry> entries)
        {
            if (entries == null)
                return;

            RunOnUiThread(() =>
            {
                EbxAssetEntry asset = AssetEntry as EbxAssetEntry;
                if (asset == null)
                    return;
                foreach (AssetEntry entry in entries)
                {
                    if (entry != null)
                        asset.LinkAsset(entry);
                }
            });
        }

        private void SoundExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack track))
                return;

            // An export writes the decoded audio, so a clip that has not been decoded yet is decoded
            // here rather than exported as an empty file.
            if (!EnsureSelectedSamples(out int exportIndex, out SoundDataTrack exportTrack))
                return;

            track = exportTrack;

            FrostySaveFileDialog sfd = new FrostySaveFileDialog("Save WAV File", "WAV file (*.wav)|*.wav", "Sound", SafeFileName(track.Name, track.Index) + ".wav");

            if (!sfd.ShowDialog())
                return;

            Exception failure = RunTask("Exporting Sound", task =>
            {
                File.WriteAllBytes(sfd.FileName, SoundEdits.EncodeWav(track.Samples, track.ChannelCount, track.SampleRate));
            });

            if (failure == null)
                App.Logger.Log("Exported {0} to {1}", AssetEntry?.Name ?? "the track", sfd.FileName);
        }

        /// <summary>
        /// The name an exported file should carry: the track's own name when it has one, since that is
        /// what makes a folder of exports readable, and the clip's number when it does not.
        /// </summary>
        protected static string SafeFileName(string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Format("clip_{0:D5}", index);

            StringBuilder builder = new StringBuilder();
            foreach (char c in name)
            {
                // Only the characters a Windows file name cannot hold are replaced; everything else,
                // including the accents in a player's name, is kept.
                bool bad = c < ' ' || c == '<' || c == '>' || c == ':' || c == '"' || c == '/' || c == '\\'
                    || c == '|' || c == '?' || c == '*';
                builder.Append(bad ? '_' : c);
            }

            string cleaned = builder.ToString().Trim().TrimEnd('.');
            if (cleaned.Length == 0)
                return string.Format("clip_{0:D5}", index);

            return cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
        }

        /// <summary>
        /// True when the editor subclass contributes its own "export every track" command that does
        /// something this one cannot, so the base command steps aside instead of competing with it.
        /// </summary>
        protected virtual bool HasOwnExportAllCommand => false;

        /// <summary>
        /// Writes every loaded track to one folder as PCM WAV, which is what a bank of clips is usually
        /// wanted as. A track that carries no samples is skipped and named in the log rather than being
        /// written as a zero length file that looks like a silent clip.
        /// </summary>
        private void SoundExportAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (TracksList == null || TracksList.Count == 0)
                return;

            FrostySaveFileDialog sfd = new FrostySaveFileDialog("Pick export folder (save any name here)", "All Files (*.*)|*.*", "Sound", "tracks");
            if (!sfd.ShowDialog())
                return;

            string outputDir = System.IO.Path.GetDirectoryName(sfd.FileName);
            int written = 0, skipped = 0;

            RunTask("Exporting Tracks", task =>
            {
                List<SoundDataTrack> tracks = null;
                Dispatcher?.Invoke(() => { tracks = new List<SoundDataTrack>(TracksList); });
                if (tracks == null)
                    return;

                for (int i = 0; i < tracks.Count; i++)
                {
                    task.Update(status: "Exporting " + (i + 1) + " / " + tracks.Count,
                        progress: ((i + 1) / (double)tracks.Count) * 100.0d);

                    // Clips stored in a shared chunk are decoded here, one at a time, rather than every
                    // clip being decoded to open the tab: a bank's whole audio can be half a gigabyte.
                    if (tracks[i] != null && !tracks[i].SamplesLoaded)
                        EnsureSamples(tracks[i], i);

                    SoundDataTrack track = tracks[i];
                    if (track == null || track.ChannelCount <= 0 || track.SampleRate <= 0 || track.Samples == null || track.Samples.Length == 0)
                    {
                        skipped++;
                        App.Logger.LogWarning("track #{0} carries no audio, so nothing was written for it", i + 1);
                        continue;
                    }

                    // The file is named after the clip, which is the whole point of naming the tracks:
                    // a folder of "30502520 - Giovanni Pasquale (take 1).wav" can be used without the
                    // editor open beside it. A name that repeats gets a numbered suffix instead of
                    // silently overwriting the earlier file.
                    string fileName = SafeFileName(track.Name, track.Index > 0 ? track.Index : i + 1);
                    string filePath = System.IO.Path.Combine(outputDir, fileName + ".wav");

                    int suffix = 2;
                    while (File.Exists(filePath))
                    {
                        filePath = System.IO.Path.Combine(outputDir, fileName + " (" + suffix + ")" + ".wav");
                        suffix++;
                    }

                    try
                    {
                        File.WriteAllBytes(filePath, SoundEdits.EncodeWav(track.Samples, track.ChannelCount, track.SampleRate));
                        written++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning("track #{0} could not be written: {1}", i + 1, ex.Message);
                    }
                }
            });

            App.Logger.Log("Exported {0} of {1} tracks to {2}{3}", written, TracksList.Count, outputDir,
                skipped > 0 ? " (" + skipped + " carried no audio)" : "");
        }

        private void SoundImportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (tracksListBox.SelectedItem == null)
                return;

            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog("Import Sound", "Audio Files (*.mp3; *.wav; *.ealayer3)|*.mp3; *.wav; *.ealayer3", "Sound");
            if (ofd.ShowDialog())
            {
                RunTask("Importing track", task =>
                {
                    ImportSound(ofd, task);
                });
            }
        }

        private void SoundAddTrackMenuItem_Click(object sender, RoutedEventArgs e)
        {
            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog("Add Track", "Audio Files (*.mp3; *.wav; *.ealayer3)|*.mp3; *.wav; *.ealayer3", "Sound");

            // A format whose new clip is not a file the user picks - a bank's new take is a copy of an existing
            // clip - answers through `AddClipNeedsAudioFile`, and the picker is skipped rather than opened on a
            // file the edit has no use for.
            if (!AddClipNeedsAudioFile || ofd.ShowDialog())
            {
                // Anything only the UI thread can ask is asked here, before the task window moves the edit onto
                // its own thread: a WPF window cannot even be constructed there ("The calling thread must be
                // STA, because many UI components require this"), which is what a dialog inside AddTrack does.
                if (!PrepareAddClip())
                    return;

                RunTask("Adding track", task =>
                {
                    AddTrack(ofd, task);
                });
            }
        }

        private void SoundDeleteTrackMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (tracksListBox.SelectedItem == null)
                return;

            // Read here, on the UI thread, where the selection lives.
            int index = SelectedTrackIndex;
            if (index < 0)
                return;

            RunTask("Deleting track", task =>
            {
                DeleteTrack(index, task);
            });
        }

        /// <summary>
        /// True when this asset states its audio the `SoundWaveAsset` way: a list of runtime variations over a
        /// list of chunks, with each variation naming a run of segments whose `SamplesOffset` says where in the
        /// chunk its bytes start. That is the shape this class knows how to add to and remove from.
        /// </summary>
        protected bool TryReadVariationShape(out dynamic soundWave)
        {
            soundWave = null;
            try
            {
                dynamic root = RootObject;
                if (root == null)
                    return false;

                if (root.RuntimeVariations == null || root.Chunks == null || root.Segments == null)
                    return false;

                soundWave = root;
                return true;
            }
            catch (Exception)
            {
                // A root object without these fields answers with a binder exception, which is the answer:
                // this is not the shape.
                return false;
            }
        }

        /// <summary>
        /// Adds a clip to an asset whose audio is a runtime-variation list: a chunk holding the new clip,
        /// one segment addressing it, and one variation playing that segment.
        ///
        /// The boxes the game walks are all appended at the end, so no existing index moves: the new chunk
        /// becomes `Chunks[Count-1]`, its segment `Segments[Count-1]`, and the variation names both by their
        /// new index. The chunk joins the bundles the asset's own chunks are in, because a chunk that is in
        /// no bundle is one the game's bundle walk never reaches. `Seekable` is cleared the same way an edit
        /// clears it: the new clip carries no seek table, so the asset cannot claim every clip has one.
        /// </summary>
        protected virtual void AddTrack(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            dynamic soundWave;
            if (!TryReadVariationShape(out soundWave))
            {
                App.Logger.LogError("Add Clip: this asset does not state its audio as runtime variations, so a " +
                    "clip cannot be added to it; nothing was changed.");
                return;
            }

            task.Update(status: "Encoding the audio file...");
            byte[] audio = ConvertAudioFileToPcm16Big(ofd);
            if (audio == null || audio.Length == 0)
            {
                App.Logger.LogError("The file chosen for the new clip decoded to no audio, so nothing was written.");
                return;
            }

            int[] bundles = BundlesOfFirstLoadedChunk(soundWave);

            task.Update(status: "Adding the clip's chunk...");
            Guid chunkId = bundles != null && bundles.Length > 0
                ? App.AssetManager.AddChunk(audio, null, null, bundles)
                : App.AssetManager.AddChunk(audio);
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
            if (chunkEntry == null)
            {
                App.Logger.LogError("The audio was written as a new chunk, but the asset manager does not list it, " +
                    "so the clip cannot be named in the asset; nothing else was changed.");
                return;
            }

            dynamic soundDataChunk = TypeLibrary.CreateObject("SoundDataChunk");
            dynamic segment = TypeLibrary.CreateObject("SoundWaveVariationSegment");
            dynamic variation = TypeLibrary.CreateObject("SoundWaveRuntimeVariation");
            if (soundDataChunk == null || segment == null || variation == null)
            {
                App.Logger.LogError("The game's type library does not describe the three objects a clip needs, so " +
                    "nothing was changed.");
                return;
            }

            int segmentIndex = (int)soundWave.Segments.Count;
            int chunkIndex = (int)soundWave.Chunks.Count;

            soundDataChunk.ChunkId = chunkId;
            soundDataChunk.ChunkSize = (uint)audio.Length;
            soundWave.Chunks.Add(soundDataChunk);

            segment.SamplesOffset = 0u;
            segment.SeekTableOffset = 4294967295u;
            soundWave.Segments.Add(segment);

            variation.PersistentDataSize = 0u;
            variation.FirstSegmentIndex = (ushort)segmentIndex;
            variation.FirstSubtitleIndex = (byte)0;
            variation.SubtitleCount = (byte)0;
            variation.SegmentCount = (byte)1;
            variation.ChunkIndex = (byte)chunkIndex;
            variation.FirstLoopSegmentIndex = (byte)0;
            variation.LastLoopSegmentIndex = (byte)0;
            variation.Weight = (byte)100;
            soundWave.RuntimeVariations.Add(variation);

            soundWave.Seekable = false;

            ReloadAfterVariationEdit(task, new[] { chunkEntry });
            App.Logger.Log("Added a clip to {0}: {1:N0} bytes in a new chunk, and the asset now has {2} clip(s).",
                AssetNameOnUiThread() ?? "this asset", audio.Length, (int)soundWave.RuntimeVariations.Count);
        }

        /// <summary>
        /// Removes one clip from a runtime-variation asset: its segments, the variation that played them, and
        /// the chunk when nothing else reads it. Every variation after it renumbers its segment start, and the
        /// localization and subtitle ranges that name variations are shortened or moved by the same one row.
        ///
        /// Nothing is refused for being the last clip: an asset with no variations is an empty sound, and the
        /// format states that with three empty arrays rather than needing a row to exist.
        /// </summary>
        protected virtual void DeleteTrack(int trackIndex, FrostyTaskWindow task)
        {
            dynamic soundWave;
            if (!TryReadVariationShape(out soundWave))
            {
                App.Logger.LogError("Delete Clip: this asset does not state its audio as runtime variations, so a " +
                    "clip cannot be removed from it; nothing was changed.");
                return;
            }

            int variationCount = (int)soundWave.RuntimeVariations.Count;
            if (trackIndex < 0 || trackIndex >= variationCount)
            {
                App.Logger.LogError("There is no clip #{0} in this asset (it has {1}).", trackIndex + 1, variationCount);
                return;
            }

            dynamic gone = soundWave.RuntimeVariations[trackIndex];
            int firstSegment = (int)gone.FirstSegmentIndex;
            int segmentCount = (int)gone.SegmentCount;
            int chunkIndex = (int)gone.ChunkIndex;
            int firstSubtitle = (int)gone.FirstSubtitleIndex;
            int subtitleCount = (int)gone.SubtitleCount;

            int readersOfChunk = 0;
            foreach (dynamic other in soundWave.RuntimeVariations)
            {
                if ((int)other.ChunkIndex == chunkIndex)
                    readersOfChunk++;
            }

            task.Update(status: "Removing the clip...");

            // Subtitles first: they are addressed by the variation that is leaving, and the arrays are shared.
            for (int i = 0; i < subtitleCount; i++)
            {
                if (firstSubtitle < (int)soundWave.SubtitleStringIds.Count)
                    soundWave.SubtitleStringIds.RemoveAt(firstSubtitle);
                if (firstSubtitle < (int)soundWave.Subtitles.Count)
                    soundWave.Subtitles.RemoveAt(firstSubtitle);
            }

            for (int i = 0; i < segmentCount; i++)
            {
                if (firstSegment < (int)soundWave.Segments.Count)
                    soundWave.Segments.RemoveAt(firstSegment);
            }

            soundWave.RuntimeVariations.RemoveAt(trackIndex);

            // Every variation after the one that left moves its starts by one row; the lists are shorter now,
            // so this is a shift of the refs, not of the data.
            foreach (dynamic other in soundWave.RuntimeVariations)
            {
                if ((int)other.FirstSegmentIndex > firstSegment)
                    other.FirstSegmentIndex = (ushort)((int)other.FirstSegmentIndex - segmentCount);
                if ((int)other.FirstSubtitleIndex > firstSubtitle)
                    other.FirstSubtitleIndex = (byte)((int)other.FirstSubtitleIndex - subtitleCount);
            }

            // A chunk no variation addresses any more leaves with them, and the chunks after it renumber.
            if (readersOfChunk <= 1 && chunkIndex < (int)soundWave.Chunks.Count)
            {
                soundWave.Chunks.RemoveAt(chunkIndex);
                foreach (dynamic other in soundWave.RuntimeVariations)
                {
                    if ((int)other.ChunkIndex > chunkIndex)
                        other.ChunkIndex = (byte)((int)other.ChunkIndex - 1);
                }
            }

            ShiftVariationRanges(soundWave, trackIndex);

            ReloadAfterVariationEdit(task, null);
            App.Logger.Log("Removed clip #{0} from {1}; the asset now has {2} clip(s).",
                trackIndex + 1, AssetNameOnUiThread() ?? "this asset", (int)soundWave.RuntimeVariations.Count);
        }

        /// <summary>
        /// The localization table names a run of variations per language (`FirstVariationIndex` +
        /// `VariationCount`). Removing a variation shortens the run that holds it and moves every run after
        /// it, which is what keeps a language pointing at its own clips after a delete.
        /// </summary>
        private static void ShiftVariationRanges(dynamic soundWave, int removedIndex)
        {
            try
            {
                if (soundWave.Localization == null)
                    return;

                foreach (dynamic entry in soundWave.Localization)
                {
                    int first = (int)entry.FirstVariationIndex;
                    int count = (int)entry.VariationCount;

                    if (removedIndex < first)
                        entry.FirstVariationIndex = (ushort)(first - 1);
                    else if (removedIndex < first + count)
                        entry.VariationCount = (ushort)(count - 1);
                }
            }
            catch (Exception)
            {
                // An asset whose localization table is some other shape is left alone: the clips themselves
                // are already correct, and guessing at an unknown table is how a language loses its audio.
            }
        }

        /// <summary>The bundles the asset's first loaded chunk is in, so a new chunk joins the same ones.</summary>
        private static int[] BundlesOfFirstLoadedChunk(dynamic soundWave)
        {
            try
            {
                foreach (dynamic chunk in soundWave.Chunks)
                {
                    ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(chunk.ChunkId);
                    if (entry == null)
                        continue;

                    List<int> bundles = new List<int>(entry.Bundles);
                    foreach (int added in entry.AddedBundles)
                    {
                        if (!bundles.Contains(added))
                            bundles.Add(added);
                    }

                    return bundles.ToArray();
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>
        /// Shows the list an add or a delete left behind, marks the asset modified (which is what writes its
        /// EBX back through `ModifyEbx`) and links the chunks the edit touched.
        /// </summary>
        protected void ReloadAfterVariationEdit(FrostyTaskWindow task, IEnumerable<ChunkAssetEntry> chunks)
        {
            List<SoundDataTrack> tracks = InitialLoad(task);

            Dispatcher?.Invoke(() =>
            {
                StopPlayback();
                SetTrackList(tracks);
                RefreshBankSummary();
                ApplyFilter();

                AssetModified = true;
                InvokeOnAssetModified();

                EbxAssetEntry entry = AssetEntry as EbxAssetEntry;
                if (entry != null && chunks != null)
                {
                    foreach (ChunkAssetEntry chunk in chunks)
                        entry.LinkAsset(chunk);
                }
            });
        }

        private void SoundDeleteAllExceptMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Simple input dialog
            Window inputWindow = new Window
            {
                Title = "Keep First N Tracks",
                Width = 300,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Window.GetWindow(this)
            };
            StackPanel sp = new StackPanel { Margin = new Thickness(10) };
            TextBlock tb = new TextBlock { Text = "How many tracks to keep (from the start):" };
            TextBox input = new TextBox { Margin = new Thickness(0, 5, 0, 5), Text = "1" };
            Button okBtn = new Button { Content = "OK", Width = 60, HorizontalAlignment = HorizontalAlignment.Right };
            int keepCount = -1;
            okBtn.Click += (s2, e2) => { int.TryParse(input.Text, out keepCount); inputWindow.Close(); };
            sp.Children.Add(tb);
            sp.Children.Add(input);
            sp.Children.Add(okBtn);
            inputWindow.Content = sp;
            inputWindow.ShowDialog();

            if (keepCount <= 0)
                return;

            RunTask("Deleting tracks", task =>
            {
                KeepFirstNTracks(keepCount, task);
            });
        }

        /// <summary>
        /// Removes every clip after the first <paramref name="keepCount"/> from a runtime-variation asset, from
        /// the end down so each delete works on the list the last one left.
        /// </summary>
        protected virtual void KeepFirstNTracks(int keepCount, FrostyTaskWindow task)
        {
            dynamic soundWave;
            if (!TryReadVariationShape(out soundWave))
            {
                App.Logger.LogError("Keep First N Clips: this asset does not state its audio as runtime variations, " +
                    "so its clips cannot be removed this way; nothing was changed.");
                return;
            }

            int count = (int)soundWave.RuntimeVariations.Count;
            if (keepCount < 0 || keepCount > count)
            {
                App.Logger.LogError("There is nothing to keep: the asset has {0} clip(s) and the count given was {1}.",
                    count, keepCount);
                return;
            }

            for (int i = count - 1; i >= keepCount; i--)
            {
                task.Update(status: "Removing clip " + (i + 1) + " of " + count);
                DeleteTrack(i, task);
            }
        }

        /// <summary>Removes every clip but the one at <paramref name="keepIndex"/>, the last one first.</summary>
        protected virtual void DeleteAllExcept(int keepIndex, FrostyTaskWindow task)
        {
            dynamic soundWave;
            if (!TryReadVariationShape(out soundWave))
            {
                App.Logger.LogError("Delete All Except: this asset does not state its audio as runtime variations, " +
                    "so its clips cannot be removed this way; nothing was changed.");
                return;
            }

            int count = (int)soundWave.RuntimeVariations.Count;
            if (keepIndex < 0 || keepIndex >= count)
            {
                App.Logger.LogError("There is no clip #{0} to keep in this asset (it has {1}).", keepIndex + 1, count);
                return;
            }

            for (int i = count - 1; i >= 0; i--)
            {
                if (i == keepIndex)
                    continue;

                task.Update(status: "Removing clip " + (i + 1) + " of " + count);
                DeleteTrack(i, task);

                if (i < keepIndex)
                    keepIndex--;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Sample editing (menu wiring, apply, write-back)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// The buttons above the list.
        ///
        /// Every command in this editor used to live only in the list's right-click menu, which is where a
        /// command goes to be undiscoverable: the same handlers are offered here as buttons, so the two things
        /// a modder does most - bring audio in and take it out - are visible without knowing that a context
        /// menu exists. `Edit` opens that same menu, so there is one set of edit commands and not two.
        /// </summary>
        private void WireToolbar()
        {
            addClipButton = GetTemplateChild(PART_AddClipButton) as Button;
            if (addClipButton != null)
            {
                addClipButton.Click += SoundAddTrackMenuItem_Click;
                ToolTipService.SetShowOnDisabled(addClipButton, true);
            }

            deleteClipButton = GetTemplateChild(PART_DeleteClipButton) as Button;
            if (deleteClipButton != null)
            {
                deleteClipButton.Click += SoundDeleteTrackMenuItem_Click;
                ToolTipService.SetShowOnDisabled(deleteClipButton, true);
            }

            importButton = GetTemplateChild(PART_ImportButton) as Button;
            if (importButton != null)
            {
                importButton.Click += SoundImportMenuItem_Click;
                ToolTipService.SetShowOnDisabled(importButton, true);
            }

            exportButton = GetTemplateChild(PART_ExportButton) as Button;
            if (exportButton != null)
            {
                exportButton.Click += SoundExportMenuItem_Click;
                ToolTipService.SetShowOnDisabled(exportButton, true);
            }

            editMenuButton = GetTemplateChild(PART_EditMenuButton) as Button;
            if (editMenuButton != null)
                editMenuButton.Click += EditMenuButton_Click;

            UpdateCommandState();
        }

        /// <summary>
        /// Opens the clip menu under its button. The menu is the list's own context menu, so the two doors lead
        /// to one set of commands and one place to change them.
        /// </summary>
        private void EditMenuButton_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = tracksListBox == null ? null : tracksListBox.ContextMenu;
            if (menu == null)
                return;

            menu.PlacementTarget = editMenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>
        /// What the buttons can do right now, and why not when they cannot.
        ///
        /// The menu items have always been enabled whatever is selected, and answered with a line in the log
        /// when there was nothing to act on; a button that looks pressable and does nothing is the same defect
        /// with a more visible surface, so each of these says what it needs instead. `Add Clip` is the one that
        /// is about the *asset* rather than the selection: only a format whose own index this plugin can
        /// rewrite has it, and the tooltip says so.
        /// </summary>
        protected void UpdateCommandState()
        {
            int selected = SelectedTrackIndex;

            if (importButton != null)
            {
                importButton.IsEnabled = selected >= 0;
                importButton.ToolTip = selected >= 0
                    ? "Replace the selected clip's audio with an audio file"
                    : "Select a clip first: an import replaces the selected clip's audio";
            }

            if (exportButton != null)
            {
                exportButton.IsEnabled = selected >= 0;
                exportButton.ToolTip = selected >= 0
                    ? "Export the selected clip as a WAV file"
                    : "Select a clip first";
            }

            bool canAdd = CanAddClip;
            string addTip = canAdd ? "Add a clip to this asset from an audio file" : AddClipRefusal;

            if (addClipButton != null)
            {
                addClipButton.IsEnabled = canAdd;
                addClipButton.ToolTip = addTip;
            }

            // The menu entry is the same command, so it follows the same answer instead of staying clickable
            // while the button beside it is greyed out - which is what made "Add Clip" look available and
            // answer with a line in the log.
            if (addClipMenuItem != null)
            {
                addClipMenuItem.IsEnabled = canAdd;
                addClipMenuItem.ToolTip = addTip;
            }

            // Delete Clip answers with a sentence when it has nothing to act on, so it is greyed out the same way
            // rather than looking pressable. Unlike an add, the answer here is about the *selection* as well as the
            // asset - a delete names a row - which is why the question is asked on every update.
            string removeRefusal = RemoveClipRefusal;
            bool canRemove = selected >= 0 && removeRefusal == null;
            if (deleteClipMenuItem != null)
            {
                deleteClipMenuItem.IsEnabled = canRemove;
                deleteClipMenuItem.ToolTip = canRemove ? "Remove the selected clip and its audio" : removeRefusal;
            }

            // The same command as a button beside Add Clip, so it is visible without knowing that the list has a
            // right-click menu, and it answers with the same sentence when it cannot act.
            if (deleteClipButton != null)
            {
                deleteClipButton.IsEnabled = canRemove;
                deleteClipButton.ToolTip = canRemove ? "Remove the selected clip and its audio" : removeRefusal;
            }
        }

        /// <summary>
        /// Why `Delete Clip` cannot act on the selection, or null when it can.
        ///
        /// Null is the default, so every asset type that had this command before keeps it exactly as it was: the
        /// formats that implement a delete answer with their own sentence, and the ones that only log a warning are
        /// left to answer the same way they always did.
        /// </summary>
        protected virtual string RemoveClipRefusal
        {
            get { return null; }
        }

        /// <summary>
        /// Whether this asset can take a new clip at all.
        ///
        /// False by default, because a new clip is not a new row of the editor's list: it is a new entry in
        /// the asset's own index and new bytes in its audio, and only a format whose index this plugin knows
        /// how to rebuild can have one. A format that can says so here, rather than offering a command that
        /// answers with a warning line in the log.
        /// </summary>
        protected virtual bool CanAddClip
        {
            get { return false; }
        }

        /// <summary>What to tell the user about adding a clip, when this asset cannot take one.</summary>
        protected virtual string AddClipRefusal
        {
            get { return "Adding a clip is not supported for this asset type."; }
        }

        /// <summary>
        /// Whether a new clip comes from an audio file the user picks.
        ///
        /// True for the formats whose clip *is* that file. False for a bank, where a new take's audio is a copy
        /// of an existing clip's - a file picker would ask a question the edit cannot use the answer to.
        /// </summary>
        protected virtual bool AddClipNeedsAudioFile
        {
            get { return true; }
        }

        /// <summary>
        /// Whatever the user has to answer before an add starts - a name, a choice - asked on the UI thread.
        ///
        /// `AddTrack` runs on `FrostyTaskWindow`'s background thread, where a WPF `Window` cannot be built at all,
        /// so a format whose new clip needs a question asks it here. False abandons the command before the task
        /// window opens, which is what makes a cancelled dialog cost nothing.
        /// </summary>
        protected virtual bool PrepareAddClip()
        {
            return true;
        }

        private void WireEditMenuItem(string partName, RoutedEventHandler handler)
        {
            MenuItem mi = GetTemplateChild(partName) as MenuItem;
            if (mi != null)
                mi.Click += handler;
        }

        private void SoundEditUndoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack track))
            {
                App.Logger.LogWarning("Select a track to undo an edit on.");
                return;
            }

            int trackIndex = SelectedTrackIndex;
            if (trackIndex < 0)
                return;

            short[] previous;
            if (!EditHistory.TryUndo(trackIndex, out previous))
            {
                App.Logger.LogWarning("There is no edit to undo on this track.");
                return;
            }

            List<SoundDataTrack> reloaded = null;
            TracksNeedReload = false;
            bool written = false;

            RunTask("Undoing Edit", task =>
            {
                task.Update(status: "Writing the previous audio back...");
                if (!CommitTrackSamples(track, trackIndex, previous, task))
                    return;

                written = true;
                if (TracksNeedReload)
                {
                    task.Update(status: "Reloading the tracks this undo moved...");
                    reloaded = InitialLoad(task);
                }
            });

            if (!written)
            {
                // The step goes back on the history, so the undo can be asked for again.
                EditHistory.Push(trackIndex, previous);
                App.Logger.LogError("Undo failed: the previous audio could not be written back (see above), so nothing was undone.");
                return;
            }

            if (reloaded != null)
                ShowReloadedTracks(reloaded, trackIndex);
            else
                ShowEditedTrack(track, previous);
            App.Logger.Log("Undo edit: {0} frames restored on track #{1} ({2} more step(s) available)",
                SoundEdits.FrameCount(previous, track.ChannelCount), trackIndex + 1, EditHistory.CountFor(trackIndex));
        }

        private void SoundEditNormalizeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ApplyEdit("Normalize", "", (track, task) => SoundEdits.Normalize(track.Samples));
        }

        private void SoundEditGainMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double? gain = PromptForValue("Gain", "Gain in dB (negative attenuates):", "0");
            if (gain == null)
                return;

            double db = gain.Value;
            ApplyEdit("Gain", db.ToString("0.##") + " dB", (track, task) => SoundEdits.ApplyGain(track.Samples, db));
        }

        private void SoundEditFadeInMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double? ms = PromptForValue("Fade In", "Fade length in milliseconds:", "250");
            if (ms == null)
                return;

            int fadeMs = (int)ms.Value;
            ApplyEdit("Fade in", fadeMs + " ms",
                (track, task) => SoundEdits.FadeIn(track.Samples, track.ChannelCount, track.SampleRate, fadeMs));
        }

        private void SoundEditFadeOutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double? ms = PromptForValue("Fade Out", "Fade length in milliseconds:", "250");
            if (ms == null)
                return;

            int fadeMs = (int)ms.Value;
            ApplyEdit("Fade out", fadeMs + " ms",
                (track, task) => SoundEdits.FadeOut(track.Samples, track.ChannelCount, track.SampleRate, fadeMs));
        }

        private void SoundEditTrimSilenceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double? thresholdDb = PromptForValue("Trim Silence",
                "Silence threshold in dBFS (anything quieter is trimmed from both ends):",
                SoundEdits.DefaultSilenceThresholdDb.ToString("0.#"));
            if (thresholdDb == null)
                return;

            double threshold = thresholdDb.Value;
            ApplyEdit("Trim silence", "below " + threshold.ToString("0.#") + " dBFS",
                (track, task) => SoundEdits.TrimSilence(track.Samples, track.ChannelCount, track.SampleRate, threshold));
        }

        private void SoundEditTrimRangeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            double? start = PromptForValue("Trim To Range", "Keep from this many seconds in (blank or 0 for the start):", "0");
            double? end = PromptForValue("Trim To Range", "Keep up to this many seconds (blank for the end of the clip):", "");

            double startSeconds = start ?? 0.0;
            double endSeconds = end ?? 0.0;
            string detail = (startSeconds <= 0 ? "0" : startSeconds.ToString("0.###")) + " s .. " +
                            (endSeconds <= 0 ? "end" : endSeconds.ToString("0.###") + " s");

            ApplyEdit("Trim to range", detail,
                (track, task) => SoundEdits.TrimSeconds(track.Samples, track.ChannelCount, track.SampleRate, startSeconds, endSeconds));
        }

        private void SoundEditReverseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ApplyEdit("Reverse", "", (track, task) => SoundEdits.Reverse(track.Samples, track.ChannelCount));
        }

        /// <summary>
        /// Runs one sample-domain edit on the selected track and writes the result back to whatever
        /// carries the audio. The edit runs on the task window's own thread - a multi-megabyte
        /// decode-and-scale pass is not a UI-thread job - while the track object itself is only
        /// touched on the UI thread, which is where <see cref="InitialLoad"/> already builds it.
        /// </summary>
        private void ApplyEdit(string action, string detail, Func<SoundDataTrack, FrostyTaskWindow, short[]> edit)
        {
            if (!(tracksListBox.SelectedItem is SoundDataTrack selectedTrack))
            {
                App.Logger.LogWarning("Select a track to edit first.");
                return;
            }

            // An edit works on samples, and a bank clip has none until it is decoded. Decoding here, on
            // the UI thread and before the task window opens, keeps the track object itself on the
            // thread that owns it.
            if (!EnsureSelectedSamples(out int trackIndex, out SoundDataTrack readyTrack))
                return;

            selectedTrack = readyTrack;
            string label = action + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")");

            short[] samples = null;
            short[] previousSamples = null;
            List<SoundDataTrack> reloaded = null;
            int framesBefore = 0, framesAfter = 0;
            double peakBeforeDb = 0.0, peakAfterDb = 0.0;
            bool committed = false;

            TracksNeedReload = false;

            RunTask("Editing Track", task =>
            {
                task.Update(status: action + "...");

                short[] source = null;
                Dispatcher?.Invoke(() => { source = selectedTrack.Samples; });
                previousSamples = source;

                framesBefore = SoundEdits.FrameCount(source, selectedTrack.ChannelCount);
                peakBeforeDb = SoundEdits.PeakDb(source);

                short[] edited = edit(selectedTrack, task);
                if (edited == null)
                    edited = new short[0];

                framesAfter = SoundEdits.FrameCount(edited, selectedTrack.ChannelCount);
                peakAfterDb = SoundEdits.PeakDb(edited);

                if (framesAfter == 0)
                {
                    App.Logger.LogWarning("" + label + " would leave the track empty; nothing was written.");
                    return;
                }

                task.Update(status: "Writing " + label + "...");

                // The commit says whether the bytes reached the asset. A command that could not write
                // (the format states its audio somewhere this editor cannot address, a chunk is missing,
                // the manager refused the write) must not be followed by "edited" in the log and a history
                // entry the undo cannot honour: that is a fake action, and it was exactly what the base
                // implementation used to do by logging a warning and returning.
                if (!CommitTrackSamples(selectedTrack, trackIndex, edited, task))
                    return;

                committed = true;

                samples = edited;

                if (TracksNeedReload)
                {
                    task.Update(status: "Reloading the tracks this edit moved...");
                    reloaded = InitialLoad(task);
                }
            });

            if (!committed)
                return;

            if (reloaded != null)
            {
                // The rebuilt list holds fresh objects that already carry the edit, so it is shown
                // instead of the edited one. The reload clears the history (the list is reset), which
                // is why the snapshot is recorded after it.
                ShowReloadedTracks(reloaded, trackIndex);
                EditHistory.Push(trackIndex, previousSamples);
            }
            else
            {
                EditHistory.Push(trackIndex, previousSamples);
                ShowEditedTrack(selectedTrack, samples);
            }

            App.Logger.Log("{0}: {1} -> {2} frames, peak {3} -> {4} dBFS",
                label, framesBefore, framesAfter, FormatDb(peakBeforeDb), FormatDb(peakAfterDb));
        }

        /// <summary>Rebuilds the list from the asset, keeping the edited track selected. Safe to call from any thread.</summary>
        protected void ShowReloadedTracks(List<SoundDataTrack> tracks, int selectedIndex)
        {
            Dispatcher?.Invoke(() =>
            {
                StopPlayback();

                SetTrackList(tracks);

                // The list may be filtered, so the track is selected as an item: the list's own index is a
                // position in the filtered view and would land on the wrong clip.
                if (tracksListBox != null && selectedIndex >= 0 && selectedIndex < TracksList.Count)
                    tracksListBox.SelectedItem = TracksList[selectedIndex];

                RefreshBankSummary();
                ApplyFilter();

                AssetModified = true;
                InvokeOnAssetModified();
            });
        }

        /// <summary>Shows the samples an edit or an undo left behind, on the UI thread.</summary>
        private void ShowEditedTrack(SoundDataTrack track, short[] samples)
        {
            Dispatcher?.Invoke(() =>
            {
                StopPlayback();

                track.Samples = samples;
                track.Duration = SoundEdits.DurationSeconds(samples, track.ChannelCount, track.SampleRate);
                track.SegmentCount = 1;

                ImageSource waveform = RenderWaveform(samples);
                if (waveform != null)
                    track.WaveForm = waveform;

                AssetModified = true;
                InvokeOnAssetModified();
            });
        }

        private static string FormatDb(double db)
        {
            return double.IsNegativeInfinity(db) ? "-inf" : db.ToString("0.0");
        }

        private void StopPlayback()
        {
            if (audioPlayer == null)
                return;

            audioPlayer.SoundDispose();
            if (stopButton != null) stopButton.IsEnabled = false;
            if (playButton != null) playButton.IsEnabled = tracksListBox?.SelectedItem != null;
        }

        /// <summary>
        /// Writes edited samples back to the asset that owns the audio.
        ///
        /// The base implementation is the SoundWaveAsset rule: one track is one runtime variation,
        /// the variation's segments name a byte range of one chunk, and the encoded edit replaces
        /// exactly that range. When the variation is the chunk's only reader the range is the whole
        /// chunk; otherwise every segment after the edited ones moves by the size difference, which
        /// is what keeps the following variations readable. An asset type that stores its audio some
        /// other way overrides this rather than inheriting a rule that would corrupt it.
        ///
        /// Tracks that share the edited chunk are reloaded, because their bytes have moved underneath
        /// them (<see cref="RequireTrackReload"/>).
        /// </summary>
        protected virtual bool CommitTrackSamples(SoundDataTrack track, int trackIndex, short[] samples, FrostyTaskWindow task)
        {
            dynamic soundWave;
            dynamic variation;
            try
            {
                soundWave = RootObject;
                if (soundWave.RuntimeVariations == null || trackIndex < 0 || trackIndex >= (int)soundWave.RuntimeVariations.Count)
                {
                    App.Logger.LogError("This asset does not state its audio as runtime variations, so the edit " +
                        "has nowhere to go; nothing was changed.");
                    return false;
                }

                variation = soundWave.RuntimeVariations[trackIndex];
            }
            catch (Exception)
            {
                App.Logger.LogError("This asset does not state its audio as runtime variations, so the edit " +
                    "has nowhere to go; nothing was changed.");
                return false;
            }

            dynamic soundDataChunk = soundWave.Chunks[variation.ChunkIndex];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

            if (chunkEntry == null)
            {
                App.Logger.LogError("The chunk this track lives in is not loaded, so the edit cannot be written.");
                return false;
            }

            byte[] clip = EncodeTrack(track, samples);

            int variationsInChunk = 0;
            foreach (var rtVariation in soundWave.RuntimeVariations)
            {
                if (rtVariation.ChunkIndex == variation.ChunkIndex)
                    variationsInChunk++;
            }

            byte[] newChunk;
            if (variationsInChunk <= 1)
            {
                newChunk = clip;
            }
            else
            {
                byte[] chunkData;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                    chunkData = reader.ReadToEnd();

                int firstSegment = (int)variation.FirstSegmentIndex;
                int lastSegment = firstSegment + (int)variation.SegmentCount;

                int start = (int)soundWave.Segments[firstSegment].SamplesOffset;
                int end = lastSegment < (int)soundWave.Segments.Count
                    ? (int)soundWave.Segments[lastSegment].SamplesOffset
                    : chunkData.Length;
                end = Math.Min(end, chunkData.Length);

                int sizeDiff;
                newChunk = SpliceChunk(chunkData, start, end, clip, out sizeDiff);

                for (int i = lastSegment; i < (int)soundWave.Segments.Count; i++)
                {
                    int offset = (int)soundWave.Segments[i].SamplesOffset;
                    if (offset == 0 || offset + sizeDiff < 0)
                        continue;
                    soundWave.Segments[i].SamplesOffset = (uint)(offset + sizeDiff);
                }

                RequireTrackReload();
            }

            if (!WriteChunk(chunkEntry, newChunk, "the clip's chunk"))
                return false;

            soundDataChunk.ChunkSize = (uint)newChunk.Length;

            // An edited clip is not seekable any more, and it is a single segment again.
            soundWave.Seekable = false;
            soundWave.Segments[(int)variation.FirstSegmentIndex].SeekTableOffset = 4294967295;
            variation.SegmentCount = (byte)1;
            variation.FirstLoopSegmentIndex = (byte)0;
            variation.LastLoopSegmentIndex = (byte)0;

            LinkToAssetOnUiThread(new AssetEntry[] { chunkEntry });
            return true;
        }

        /// <summary>
        /// Writes a chunk through the SDK and reports what the SDK answered.
        ///
        /// `AssetManager.ModifyChunk` returns false when the chunk is not one the manager holds (an entry that
        /// was never registered, a chunk of an asset that was not loaded), and it does not throw. Every write
        /// in this plugin used to ignore that answer: the editor logged the edit as done, the track list showed
        /// the new audio, and the file on disk still held the old bytes. A refused write is an error here, and
        /// the caller stops instead of reporting success.
        /// </summary>
        protected bool WriteChunk(ChunkAssetEntry entry, byte[] data, string what)
        {
            if (entry == null || data == null)
            {
                App.Logger.LogError("The {0} is not loaded from the game data, so nothing was written.", what ?? "audio");
                return false;
            }

            if (!App.AssetManager.ModifyChunk(entry.Id, data))
            {
                App.Logger.LogError("The SDK refused the write to the {0} (chunk {1} is not one it manages), so " +
                    "nothing was changed.", what ?? "audio", entry.Id);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a `.res` through the SDK. `ModifyRes` cannot fail loudly - it returns nothing and silently
        /// ignores an id it does not hold - so the entry is checked first and a missing one is an error.
        /// </summary>
        protected bool WriteRes(ResAssetEntry entry, byte[] data, string what)
        {
            if (entry == null || data == null)
            {
                App.Logger.LogError("The {0} is not loaded from the game data, so nothing was written.", what ?? ".res");
                return false;
            }

            App.AssetManager.ModifyRes(entry.ResRid, data);
            return true;
        }

        /// <summary>Encodes a track the way the chunk/res writers expect to find it.</summary>
        protected static byte[] EncodeTrack(SoundDataTrack track, short[] samples)
        {
            return SoundEdits.EncodePcm16Big(samples, Math.Max(1, track.ChannelCount), Math.Max(1, track.SampleRate));
        }

        /// <summary>
        /// Replaces chunk bytes <paramref name="start"/>..<paramref name="end"/> with one clip's
        /// encoded bytes, returning the whole chunk. <paramref name="sizeDiff"/> is what every offset
        /// after the clip has to move by. The rule itself lives in <see cref="SoundEdits.SpliceClip"/>,
        /// which is where it is checked.
        /// </summary>
        protected static byte[] SpliceChunk(byte[] chunkData, int start, int end, byte[] clip, out int sizeDiff)
        {
            return SoundEdits.SpliceClip(chunkData, start, end, clip, out sizeDiff);
        }

        /// <summary>
        /// A shared waveform image for an edited track, so an edit shows what it did. Deliberately
        /// tolerant: a track too long or too odd to render keeps the waveform it had rather than
        /// failing the edit that already succeeded.
        /// </summary>
        protected static ImageSource RenderWaveform(short[] samples)
        {
            if (samples == null || samples.Length == 0)
                return null;

            try
            {
                System.Drawing.Color topSpacerColor = System.Drawing.Color.FromArgb(64, 83, 22, 3);
                SoundCloudBlockWaveFormSettings settings = new SoundCloudBlockWaveFormSettings(
                    System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacerColor,
                    System.Drawing.Color.FromArgb(196, 79, 26, 0), System.Drawing.Color.FromArgb(64, 79, 79, 79))
                {
                    Name = "SoundCloud Orange Transparent Blocks",
                    PixelsPerPeak = 2,
                    SpacerPixels = 1,
                    TopSpacerGradientStartColor = topSpacerColor,
                    BackgroundColor = System.Drawing.Color.Transparent,
                    Width = 800,
                    TopHeight = 50,
                    BottomHeight = 30,
                };

                WaveFormRenderer renderer = new WaveFormRenderer();
                using (System.Drawing.Image image = renderer.Render(samples, new MaxPeakProvider(), settings))
                using (MemoryStream ms = new MemoryStream())
                {
                    image.Save(ms, ImageFormat.Png);
                    ms.Seek(0, SeekOrigin.Begin);

                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = ms;
                    bitmapImage.EndInit();

                    RenderTargetBitmap target = new RenderTargetBitmap(bitmapImage.PixelWidth, bitmapImage.PixelHeight,
                        bitmapImage.DpiX, bitmapImage.DpiY, PixelFormats.Pbgra32);
                    DrawingVisual visual = new DrawingVisual();

                    using (DrawingContext r = visual.RenderOpen())
                        r.DrawImage(bitmapImage, new Rect(0, 0, bitmapImage.Width, bitmapImage.Height));

                    target.Render(visual);
                    target.Freeze();
                    return target;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// One-number prompt, the same shape the "Keep First N Tracks" command already used, for
        /// edits that need a length or a level rather than a file.
        /// </summary>
        protected double? PromptForValue(string title, string label, string initialText)
        {
            Window inputWindow = new Window
            {
                Title = title,
                Width = 380,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Window.GetWindow(this)
            };

            StackPanel sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            TextBox input = new TextBox { Margin = new Thickness(0, 5, 0, 5), Text = initialText };
            sp.Children.Add(input);

            Button okBtn = new Button { Content = "OK", Width = 60, HorizontalAlignment = HorizontalAlignment.Right };
            double? result = null;
            bool wasBlank = false;
            okBtn.Click += (s2, e2) =>
            {
                double parsed;
                if (double.TryParse(input.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture, out parsed))
                    result = parsed;
                else
                    wasBlank = string.IsNullOrWhiteSpace(input.Text);
                inputWindow.Close();
            };
            sp.Children.Add(okBtn);

            inputWindow.Content = sp;
            inputWindow.ShowDialog();

            // A blank field is an answer for the commands whose label says so ("blank for the end of the
            // clip"); text that is neither blank nor a number is not, and says so rather than being
            // silently read as no bound at all.
            if (result == null && !wasBlank)
                App.Logger.LogWarning("" + title + ": that is not a number, so nothing was changed.");

            return result;
        }

        /// <summary>
        /// A one-line text prompt, the same shape as the number prompt, for renaming a track. Enter
        /// accepts and Escape cancels, because a rename is a label and not a commitment.
        /// </summary>
        protected string PromptForText(string title, string label, string initialText)
        {
            Window inputWindow = new Window
            {
                Title = title,
                Width = 460,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Window.GetWindow(this)
            };

            StackPanel sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });

            TextBox input = new TextBox { Margin = new Thickness(0, 5, 0, 5), Text = initialText ?? "" };
            input.SelectAll();
            sp.Children.Add(input);

            Button okBtn = new Button { Content = "OK", Width = 60, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            string result = null;
            okBtn.Click += (s2, e2) => { result = input.Text; inputWindow.Close(); };
            sp.Children.Add(okBtn);

            inputWindow.Content = sp;
            inputWindow.ShowDialog();

            return result;
        }

        /// <summary>
        /// The one rule for turning a picked file into the stream a chunk or a resource takes: a WAV or
        /// an MP3 is decoded and re-encoded by the same encoder an edit uses, keeping its own channel
        /// count, and an .ealayer3 file is already in the shape the engine reads.
        /// </summary>
        protected static byte[] ConvertAudioFileToPcm16Big(FrostyOpenFileDialog ofd)
        {
            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                ofd.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                int channels, sampleRate;
                byte[] encoded = SoundEdits.EncodePcm16BigFromAudioFile(ofd.FileName, out channels, out sampleRate);
                App.Logger.Log("Importing {0} at {1} Hz, {2} channel(s)", System.IO.Path.GetFileName(ofd.FileName), sampleRate, channels);
                return encoded;
            }

            if (ofd.FileName.EndsWith(".ealayer3", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(ofd.FileName);

            throw new FileFormatException();
        }

        protected virtual void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            byte[] resultBuf = ConvertAudioFileToPcm16Big(ofd);

            int index = 0;
            Dispatcher?.Invoke(() => { index = SelectedTrackIndex; });

            dynamic soundWave = RootObject;
            dynamic variation = soundWave.RuntimeVariations[index];
            dynamic soundDataChunk = soundWave.Chunks[variation.ChunkIndex];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

            int[] variationsPerChunk = new int[(int)soundWave.Chunks.Count];
            foreach (var rtVariation in soundWave.RuntimeVariations)
            {
                variationsPerChunk[rtVariation.ChunkIndex]++;
            }

            //bool modify = true;
            //if (modify)
            //{
            if (variationsPerChunk[variation.ChunkIndex] > 1)
            {
                using (NativeReader chunkReader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                {
                    IEnumerable<byte> buf;
                    if (variation.SegmentCount == soundWave.Segments.Count)
                    {
                        // The variation is every segment the asset has, so the clip is the whole chunk and the
                        // import replaces it whole. This branch used to read the *old* chunk back into `buf` and
                        // hand that to the write: the import reported itself as done, the chunk was rewritten
                        // with the bytes it already held, and the file never changed.
                        buf = resultBuf;
                    }
                    else
                    {
                        buf = variation.FirstSegmentIndex != 0 ? chunkReader.ReadBytes((int)soundWave.Segments[variation.FirstSegmentIndex].SamplesOffset).Concat(resultBuf) : resultBuf;

                        if (variation.FirstSegmentIndex + variation.SegmentCount < soundWave.Segments.Count) // variation does not contain the final segments
                        {
                            chunkReader.Position = soundWave.Segments[variation.FirstSegmentIndex + variation.SegmentCount].SamplesOffset;
                            buf = buf.Concat(chunkReader.ReadToEnd()); // append the rest of the data

                            int sizeDiff = resultBuf.Length - ((int)soundWave.Segments[variation.FirstSegmentIndex + variation.SegmentCount].SamplesOffset - (int)soundWave.Segments[variation.FirstSegmentIndex].SamplesOffset);

                            for (int i = variation.FirstSegmentIndex + 1; i < soundWave.Segments.Count; i++)
                            {
                                if (soundWave.Segments[i].SamplesOffset == 0)
                                    break;

                                soundWave.Segments[i].SamplesOffset += sizeDiff;
                            }
                        }
                    }

                    resultBuf = buf.ToArray();
                }
            }

            if (!WriteChunk(chunkEntry, resultBuf, "clip's chunk"))
                return;

            soundDataChunk.ChunkSize = (uint)resultBuf.Length;

            // disable seekable data, not supported
            soundWave.Seekable = false;
            //soundWave.Segments[variation.FirstSegmentIndex].SamplesOffset = 0;
            soundWave.Segments[variation.FirstSegmentIndex].SeekTableOffset = 4294967295;

            variation.SegmentCount = (byte)1;
            variation.FirstLoopSegmentIndex = (byte)0;
            variation.LastLoopSegmentIndex = (byte)0;

            //}
            //else // new chunk code
            //{
            //    Guid chunkId = App.AssetManager.AddChunk(resultBuf);

            //    ChunkAssetEntry newEntry = App.AssetManager.GetChunkEntry(chunkId);
            //    newEntry.AddToBundles(chunkEntry.Bundles);

            //    soundDataChunk = TypeLibrary.CreateObject("SoundDataChunk");
            //    soundDataChunk.ChunkId = chunkId;

            //    soundWave.Chunks.Add(soundDataChunk);

            //    dynamic segment = TypeLibrary.CreateObject("SoundWaveVariationSegment");
            //    segment.SeekTableOffset = 4294967295;
            //    soundWave.Segments.Add(segment);

            //    dynamic variation = TypeLibrary.CreateObject("SoundWaveRuntimeVariation");
            //    variation.FirstSegmentIndex = (ushort)(soundWave.Segments.Count - 1);
            //    variation.SegmentCount = (byte)1;
            //    variation.ChunkIndex = (byte)(soundWave.Chunks.Count - 1);
            //    variation.Weight = (byte)100;
            //    soundWave.RuntimeVariations.Add(variation);
            //}

            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();

            List<SoundDataTrack> tracks = InitialLoad(task);

            Dispatcher?.Invoke(() =>
            {
                // mark asset as modified and link the chunk
                AssetModified = true;
                InvokeOnAssetModified();
                EbxAssetEntry assetEntry = AssetEntry as EbxAssetEntry;
                assetEntry.LinkAsset(chunkEntry);

                SetTrackList(tracks);
            });
        }

    }
}
