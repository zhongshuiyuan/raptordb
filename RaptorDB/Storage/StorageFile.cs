using System;
using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace RaptorDB
{
    internal class StorageFile<T>
    {
        FileStream _datawrite;
        FileStream _recfilewrite;
        FileStream _recfileread = null;
        FileStream _dataread = null;

        private bool _BackgroundWrites = false;
        private string _filename = "";
        private string _recfilename = "";
        private int _lastRecordNum = 0;
        private long _lastWriteOffset = 0;
        IGetBytes<T> _T = null;

        public static byte[] _fileheader = { (byte)'M', (byte)'G', (byte)'D', (byte)'B',
                                              0, // 4 -- not used,
                                              0  // 5 -- not used
                                           };

        public static byte[] _rowheader = { (byte)'M', (byte)'G', (byte)'R' ,
                                           0,               // 3     [keylen]
                                           0,0,0,0,0,0,0,0, // 4-11  [datetime] 8 bytes = insert time
                                           0,0,0,0,         // 12-15 [data length] 4 bytes
                                           0,               // 16 -- [flags] = 1 : isDeletd:1
                                                            //                 2 : isCompressed:1
                                                            //                 
                                                            //                 
                                           0                // 17 -- [crc] = header crc check
                                       };
        private enum HDR_POS
        {
            KeyLen = 3,
            DateTime = 4,
            DataLength = 12,
            Flags = 16,
            CRC = 17
        }

        public bool SkipDateTime = false;

        public StorageFile(string filename, bool backgroundwrites)
        {
            _BackgroundWrites = backgroundwrites;
            _T = RDBDataType<T>.ByteHandler();
            _filename = filename;
            if (File.Exists(filename) == false)
                _datawrite = new FileStream(filename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            else
                _datawrite = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            // load rec pointers
            _recfilename = filename.Substring(0, filename.LastIndexOf('.')) + ".mgrec";
            if (File.Exists(_recfilename) == false)
                _recfilewrite = new FileStream(_recfilename, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            else
                _recfilewrite = new FileStream(_recfilename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            if (_datawrite.Length == 0)
            {
                // new file
                _datawrite.Write(_fileheader, 0, _fileheader.Length);
                _datawrite.Flush();
            }

            _lastRecordNum = (int)(_recfilewrite.Length / 8);
            _recfilewrite.Seek(0L, SeekOrigin.End);
            _lastWriteOffset = _datawrite.Seek(0L, SeekOrigin.End);
        }

        public int Count()
        {
            return (int)(_recfilewrite.Length >> 3);
        }

        #region [  traverse  ]
        public IEnumerable<KeyValuePair<T, byte[]>> Traverse()
        {
            long offset = 0;
            offset = _fileheader.Length;

            while (offset < _datawrite.Length)
            {
                long pointer = offset;
                byte[] key;
                bool deleted = false;
                offset = NextOffset(offset, out key, out deleted);
                KeyValuePair<T, byte[]> kv = new KeyValuePair<T, byte[]>(_T.GetObject(key, 0, key.Length), internalReadData(pointer));
                if (deleted == false)
                    yield return kv;
            }
        }

        private long NextOffset(long curroffset, out byte[] key, out bool isdeleted)
        {
            isdeleted = false;
            if (_dataread == null)
                _dataread = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            {
                long next = _dataread.Length;
                // seek offset in file
                byte[] hdr = new byte[_rowheader.Length];
                _dataread.Seek(curroffset, System.IO.SeekOrigin.Begin);
                // read header
                _dataread.Read(hdr, 0, _rowheader.Length);
                key = new byte[hdr[(int)HDR_POS.KeyLen]];
                _dataread.Read(key, 0, hdr[(int)HDR_POS.KeyLen]);
                // check header
                if (CheckHeader(hdr))
                {
                    next = curroffset + hdr.Length + Helper.ToInt32(hdr, (int)HDR_POS.DataLength) + hdr[(int)HDR_POS.KeyLen];
                    isdeleted = isDeleted(hdr);
                }

                return next;
            }
        }
        #endregion

        public int WriteData(T key, byte[] data, bool deleted)
        {
            byte[] k = _T.GetBytes(key);
            int kl = k.Length;

            // seek end of file
            long offset = _lastWriteOffset;
            byte[] hdr = CreateRowHeader(kl, (data == null ? 0 : data.Length));
            if (deleted)
                hdr[(int)HDR_POS.Flags] = (byte)1;
            // write header info
            _datawrite.Write(hdr, 0, hdr.Length);
            // write key
            _datawrite.Write(k, 0, kl);
            if (data != null)
            {
                // write data block
                _datawrite.Write(data, 0, data.Length);
                _lastWriteOffset += data.Length;
                if (Global.FlushStorageFileImmetiatley)
                    _datawrite.Flush();
            }
            // update pointer
            _lastWriteOffset += hdr.Length;
            _lastWriteOffset += kl;
            // return starting offset -> recno
            int recno = _lastRecordNum++;
            _recfilewrite.Write(Helper.GetBytes(offset, false), 0, 8);
            if (Global.FlushStorageFileImmetiatley)
            {
                _datawrite.Flush();
                _recfilewrite.Flush();
            }
            return recno;
        }

        private byte[] CreateRowHeader(int keylen, int datalen)
        {
            byte[] rh = new byte[_rowheader.Length];
            Buffer.BlockCopy(_rowheader, 0, rh, 0, rh.Length);
            rh[3] = (byte)keylen;
            if (SkipDateTime == false)
                Buffer.BlockCopy(Helper.GetBytes(FastDateTime.Now.Ticks, false), 0, rh, 4, 4);
            Buffer.BlockCopy(Helper.GetBytes(datalen, false), 0, rh, 12, 4);

            return rh;
        }

        public byte[] ReadData(int recnum)
        {
            if (_BackgroundWrites == false) // kludge
            {
                _datawrite.Flush();
                _recfilewrite.Flush();
            }
            long off = recnum * 8;
            if (_recfileread == null)
                _recfileread = new FileStream(_recfilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            byte[] b = new byte[8];

            _recfileread.Seek(off, SeekOrigin.Begin);
            _recfileread.Read(b, 0, 8);
            off = Helper.ToInt64(b, 0);

            return internalReadData(off);
        }

        private byte[] internalReadData(long offset)
        {
            if (_dataread == null)
                _dataread = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // seek offset in file
            byte[] hdr = new byte[_rowheader.Length];
            _dataread.Seek(offset, System.IO.SeekOrigin.Begin);
            // read header
            _dataread.Read(hdr, 0, _rowheader.Length);
            // check header
            if (CheckHeader(hdr))
            {
                // skip key bytes
                _dataread.Seek(hdr[(int)HDR_POS.KeyLen], System.IO.SeekOrigin.Current);
                int dl = Helper.ToInt32(hdr, (int)HDR_POS.DataLength);
                byte[] data = new byte[dl];
                // read data block
                _dataread.Read(data, 0, dl);
                return data;
            }
            else
                throw new Exception("data header error at offset : " + offset + " data file size = " + _dataread.Length);
        }

        private bool CheckHeader(byte[] hdr)
        {
            if (hdr[0] == (byte)'M' && hdr[1] == (byte)'G' && hdr[2] == (byte)'R' && hdr[(int)HDR_POS.CRC] == (byte)0)
                return true;
            return false;
        }

        public void Shutdown()
        {
            FlushClose(_dataread);
            FlushClose(_recfileread);
            FlushClose(_recfilewrite);
            FlushClose(_datawrite);

            _dataread = null;
            _recfileread = null;
            _recfilewrite = null;
            _datawrite = null;
        }

        private void FlushClose(FileStream st)
        {
            if (st != null)
            {
                st.Flush(true);
                st.Close();
            }
        }

        internal T GetKey(int recnum, out bool deleted)
        {
            deleted = false;
            long off = recnum * 8;
            if (_recfileread == null)
                _recfileread = new FileStream(_recfilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            byte[] b = new byte[8];

            _recfileread.Seek(off, SeekOrigin.Begin);
            _recfileread.Read(b, 0, 8);
            off = Helper.ToInt64(b, 0);

            if (_dataread == null)
                _dataread = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // seek offset in file
            byte[] hdr = new byte[_rowheader.Length];
            _dataread.Seek(off, System.IO.SeekOrigin.Begin);
            // read header
            _dataread.Read(hdr, 0, _rowheader.Length);

            if (CheckHeader(hdr))
            {
                deleted = isDeleted(hdr);
                byte kl = hdr[3];
                byte[] kbyte = new byte[kl];

                _dataread.Read(kbyte, 0, kl);
                return _T.GetObject(kbyte, 0, kl);
            }

            return default(T);
        }

        private bool isDeleted(byte[] hdr)
        {
            return (hdr[(int)HDR_POS.Flags] & (byte)1) > 0;
        }
    }
}