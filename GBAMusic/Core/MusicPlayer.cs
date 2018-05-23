﻿using MicroLibrary;
using System;
using System.Collections.Generic;
using static GBAMusic.Core.M4AStructs;

namespace GBAMusic.Core
{
    enum State
    {
        Playing,
        Paused,
        Stopped
    }

    internal class MusicPlayer
    {
        FMOD.System system;
        FMOD.ChannelGroup parentGroup;

        Instrument[] instruments;
        Track[] tracks;
        Dictionary<uint, FMOD.Sound> sounds;

        ushort tempo;
        MicroTimer timer;

        internal MusicPlayer()
        {
            FMOD.Factory.System_Create(out system);
            system.init(32, FMOD.INITFLAGS.NORMAL, (IntPtr)0);
            system.createChannelGroup(null, out parentGroup);
            parentGroup.setVolume(0.5f);

            instruments = new Instrument[32]; // 28 Directsound, 4 GB
            for (int i = 0; i < 32; i++)
            {
                instruments[i] = new Instrument();
            }
            sounds = new Dictionary<uint, FMOD.Sound>();
            tracks = new Track[16];
            for (int i = 0; i < 16; i++)
            {
                tracks[i] = new Track(system, parentGroup);
            }

            timer = new MicroTimer();
            timer.MicroTimerElapsed += (o, e) => PlayLoop();
        }

        SongHeader header;
        VoiceTable voiceTable;

        internal State State { get; private set; }
        internal delegate void SongEndedEvent();
        internal event SongEndedEvent SongEnded;

        //uint songTable = 0x1E866BC; // PMD
        uint songTable = 0x06B49F0; // Emerald
        internal void Play(ushort song)
        {
            Stop();
            header = ROM.Instance.ReadStruct<SongHeader>(ROM.Instance.ReadPointer(songTable + ((uint)8 * song)));
            Array.Resize(ref header.Tracks, header.NumTracks); // Not really necessary
            for (int i = 0; i < header.NumTracks; i++)
                tracks[i].Init(header.Tracks[i]);
            voiceTable = new VoiceTable();
            voiceTable.LoadDirectSamples(header.VoiceTable, system, sounds);

            SetTempo(120);
            timer.Start();
            State = State.Playing;
        }
        internal void Pause()
        {
            if (State == State.Paused)
            {
                timer.Start();
                State = State.Playing;
            }
            else
            {
                Stop();
                State = State.Paused;
            }
        }
        internal void Stop()
        {
            timer.Stop();
            foreach (Instrument i in instruments)
                i.Stop();
            State = State.Stopped;
        }
        void PlayLoop()
        {
            bool allStopped = true;
            for (int i = 0; i < header.NumTracks; i++)
            {
                Track track = tracks[i];
                if (!track.Stopped)
                    allStopped = false;
                while (track.Delay == 0 && !track.Stopped)
                    ExecuteNext(track);
                track.Tick();
            }
            system.update();
            if (allStopped)
            {
                Stop();
                SongEnded?.Invoke();
            }
        }
        internal (ushort, uint[], byte[], byte[], byte[]) GetTrackStates()
        {
            var positions = new uint[header.NumTracks];
            var volumes = new byte[header.NumTracks];
            var delays = new byte[header.NumTracks];
            var notes = new byte[header.NumTracks];
            for (int i = 0; i < header.NumTracks; i++)
            {
                positions[i] = tracks[i].Position;
                volumes[i] = tracks[i].Volume;
                delays[i] = tracks[i].Delay;
                notes[i] = tracks[i].PrevNote;
            }
            return (tempo, positions, volumes, delays, notes);
        }

        void SetTempo(ushort t)
        {
            if (t > 510) return;
            tempo = t;
            timer.Interval = (long)(2.5 * 1000 * 1000) / t;
        }
        byte WaitFromCMD(byte startCMD, byte cmd)
        {
            byte[] added = { 4, 4, 2, 2 };
            byte wait = (byte)(cmd - startCMD);
            byte add = wait > 24 ? (byte)24 : wait;
            for (int i = 24 + 1; i <= wait; i++)
                add += added[i % 4];
            return add;
        }

        Instrument FindInstrument(byte newPriority)
        {
            Instrument instrument = null;
            for (int i = 0; i < 28; i++)
                if (!instruments[i].Playing)
                {
                    instrument = instruments[i];
                    break;
                }

            if (instrument == null) // None free
                for (int i = 0; i < 28; i++)
                    if (instruments[i].Releasing)
                        instrument = instruments[i];

            if (instrument == null) // None releasing
                for (int i = 0; i < 28; i++)
                    if (newPriority > instruments[i].Track.Priority)
                        instrument = instruments[i];

            if (instrument == null) // None available, so get newest
                instrument = instruments[27];
            instrument.Stop();
            return instrument;
        }
        void PlayNote(Track track, byte note, byte velocity, byte addedDelay = 0)
        {
            track.PrevNote = note;
            track.PrevVelocity = velocity;

            Instrument instrument = null;
            SVoice sVoice = voiceTable[track.Voice].Instrument; // Should be overwritten if something else is to be played

            switch (sVoice.VoiceType)
            {
                case 0x0:
                case 0x8:
                    instrument = FindInstrument(track.Priority);
                    break;
                case 0x1:
                case 0x9:
                    instrument = instruments[28];
                    break;
                case 0x2:
                case 0xA:
                    instrument = instruments[29];
                    break;
                case 0x3:
                case 0xB:
                    instrument = instruments[30];
                    break;
                case 0x4:
                case 0xC:
                    instrument = instruments[31];
                    break;
                case 0x40:
                    instrument = FindInstrument(track.Priority);
                    var split = (KeySplit)sVoice;
                    var multi = (Multi)voiceTable[track.Voice];
                    byte ins = ROM.Instance.ReadByte(split.Keys + note);
                    sVoice = multi.Table[ins].Instrument;
                    break;
                case 0x80:
                    instrument = FindInstrument(track.Priority); // remove when I can
                    var drum = (Drum)voiceTable[track.Voice];
                    //drum.Table[note].Instrument;
                    break;
            }
            instrument.Play(track, system, sounds, sVoice, track.PrevCmd == 0xCF ? (byte)255 : WaitFromCMD(0xD0, track.PrevCmd));
        }

        void ExecuteNext(Track track)
        {
            byte cmd = track.ReadByte();
            #region TIE & Notes
            if (track.PrevCmd < 0xCF && cmd < 0x80)
            {
                switch (track.PrevCmd)
                {
                    case 0xBD: track.Voice = cmd; break;
                    case 0xBE: track.SetVolume(cmd); break;
                    case 0xBF: track.SetPan(cmd); break;
                    case 0xC0: track.SetBend(cmd); break;
                    case 0xC1: track.SetBendRange(cmd); break;
                    case 0xC4: track.SetMODDepth(cmd); break;
                    case 0xC5: track.SetMODType(cmd); break;
                    case 0xCD: track.ReadByte(); break; // This command takes an argument
                }
            }
            else if (track.PrevCmd >= 0xCF && cmd < 0x80)
            {
                var o = track.Position;
                byte peek1 = track.ReadByte(),
                    peek2 = track.ReadByte();
                track.SetOffset(o);
                if (peek1 >= 128) PlayNote(track, cmd, track.PrevVelocity);
                else if (peek2 >= 128) PlayNote(track, cmd, track.ReadByte());
                else PlayNote(track, cmd, track.ReadByte(), track.ReadByte());
            }
            else if (cmd >= 0xCF)
            {
                track.PrevCmd = cmd;
                var o = track.Position;
                byte peek1 = track.ReadByte(),
                    peek2 = track.ReadByte(),
                    peek3 = track.ReadByte();
                track.SetOffset(o);
                if (peek1 >= 128) PlayNote(track, track.PrevNote, track.PrevVelocity);
                else if (peek2 >= 128) PlayNote(track, track.ReadByte(), track.PrevVelocity);
                else if (peek3 >= 128) PlayNote(track, track.ReadByte(), track.ReadByte());
                else PlayNote(track, track.ReadByte(), track.ReadByte(), track.ReadByte());
            }
            #endregion
            #region Waits
            else if (cmd >= 0x80 && cmd <= 0xB0)
            {
                track.Delay = WaitFromCMD(0x80, cmd);
            }
            #endregion
            else if (cmd > 0xB0 && cmd < 0xCF)
            {
                track.PrevCmd = cmd;
                switch (cmd)
                {
                    case 0xB1: track.Stopped = true; break; // FINE
                    case 0xB2: track.SetOffset(track.ReadPointer()); break; // GOTO
                    case 0xB3: // PATT
                        uint jump = track.ReadPointer();
                        track.EndOfPattern = track.Position;
                        track.SetOffset(jump);
                        break;
                    case 0xB4: // PEND
                        if (track.EndOfPattern != 0)
                        {
                            track.SetOffset(track.EndOfPattern);
                            track.EndOfPattern = 0;
                        }
                        break;
                    case 0xB5: track.ReadByte(); break; // REPT
                    case 0xB9: track.ReadByte(); track.ReadByte(); track.ReadByte(); break; // MEMACC
                    case 0xBA: track.SetPriority(track.ReadByte()); break; // PRIO
                    case 0xBB: SetTempo((ushort)(track.ReadByte() * 2)); break; // TEMPO
                    case 0xBC: track.ReadByte(); break; // KEYSH
                    case 0xBD: track.Voice = cmd; break;
                    case 0xBE: track.SetVolume(track.ReadByte()); break;
                    case 0xBF: track.SetPan(track.ReadByte()); break;
                    case 0xC0: track.SetBend(track.ReadByte()); break;
                    case 0xC1: track.SetBendRange(track.ReadByte()); break;
                    case 0xC2: track.ReadByte(); break; // LFOS
                    case 0xC3: track.ReadByte(); break; // LFODL
                    case 0xC4: track.SetMODDepth(track.ReadByte()); break;
                    case 0xC5: track.SetMODType(track.ReadByte()); break;
                    case 0xC8: track.ReadByte(); break; // TUNE
                    case 0xCD: track.ReadByte(); track.ReadByte(); break; // XCMD
                    case 0xCE: // EOT
                        if (track.PeekByte() < 128)
                        {
                            byte whichOne = track.ReadByte();
                            // Unfinished
                        }
                        break;
                }
            }
        }
    }
}
