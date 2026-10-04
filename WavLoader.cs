using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TraditionalRWR
{
    // Shared embedded-WAV-to-AudioClip loader, used by each RWR audio pack
    // (KaceyTronicAudio, VTOLVRAudio, ...). Unity's own asset-import
    // pipeline doesn't apply to anything loaded at runtime like this, so
    // each WAV's PCM data is parsed by hand into an AudioClip instead of
    // relying on an importer.
    internal static class WavLoader
    {
        internal static AudioClip LoadEmbeddedClip(string resourceName, string clipName)
        {
            using (Stream stream = typeof(WavLoader).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return null;
                }

                byte[] bytes = new byte[stream.Length];
                int totalRead = 0;
                while (totalRead < bytes.Length)
                {
                    int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    totalRead += read;
                }

                return WavToAudioClip(bytes, clipName);
            }
        }

        // Minimal PCM WAV decoder -- every pack's clips are confirmed
        // 16-bit PCM, no exotic chunks, but this scans chunks generically
        // (rather than assuming a fixed 44-byte header) so it stays correct
        // if a pack is ever re-exported with extra metadata chunks, and
        // regardless of channel count (KaceyTronic's set is stereo, VTOLVR's
        // is mono).
        private static AudioClip WavToAudioClip(byte[] fileBytes, string clipName)
        {
            if (fileBytes == null || fileBytes.Length < 12
                || fileBytes[0] != 'R' || fileBytes[1] != 'I' || fileBytes[2] != 'F' || fileBytes[3] != 'F')
            {
                return null;
            }

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            int pos = 12; // past "RIFF"<size>"WAVE"
            while (pos + 8 <= fileBytes.Length)
            {
                string chunkId = Encoding.ASCII.GetString(fileBytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);
                int chunkDataStart = pos + 8;
                if (chunkDataStart + chunkSize > fileBytes.Length)
                {
                    break;
                }

                if (chunkId == "fmt ")
                {
                    channels = BitConverter.ToInt16(fileBytes, chunkDataStart + 2);
                    sampleRate = BitConverter.ToInt32(fileBytes, chunkDataStart + 4);
                    bitsPerSample = BitConverter.ToInt16(fileBytes, chunkDataStart + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataStart;
                    dataLength = chunkSize;
                }

                // Chunks are word-aligned -- an odd-sized chunk has a padding byte.
                pos = chunkDataStart + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample != 16)
            {
                return null;
            }

            const int bytesPerSample = 2;
            int sampleCount = dataLength / bytesPerSample;
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short raw = BitConverter.ToInt16(fileBytes, dataOffset + i * bytesPerSample);
                samples[i] = raw / 32768f;
            }

            AudioClip clip = AudioClip.Create(clipName, sampleCount / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
