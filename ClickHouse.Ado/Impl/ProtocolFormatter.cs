﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClickHouse.Ado.Impl.Compress;
using ClickHouse.Ado.Impl.Data;
using ClickHouse.Ado.Impl.Settings;

namespace ClickHouse.Ado.Impl {
    internal class ProtocolFormatter {
        private static readonly Regex NameRegex = new Regex("^[a-zA-Z_][0-9a-zA-Z_]*$", RegexOptions.Compiled);

        private readonly ClickHouseConnection _owner;

        /// <summary>
        ///     Underlaying stream, usually NetworkStream.
        /// </summary>
        private readonly Stream _baseStream;

        private readonly Func<bool> _poll;
        private readonly int _socketTimeout;

        private Compressor _compressor;

        /// <summary>
        ///     Compressed stream, !=null indicated that compression/decompression has beed started.
        /// </summary>
        private Stream _compStream;

        private ClickHouseConnectionSettings _connectionSettings;

        /// <summary>
        ///     Stream to write to/read from, either _baseStream or _compStream.
        /// </summary>
        private Stream _ioStream;

        internal ProtocolFormatter(ClickHouseConnection owner, Stream baseStream, ClientInfo clientInfo, Func<bool> poll, int socketTimeout) {
            _owner = owner;
            _baseStream = baseStream;
            _ioStream = baseStream;
            _poll = poll;
            _socketTimeout = socketTimeout;
            ClientInfo = clientInfo;
        }

        public ServerInfo ServerInfo { get; set; }
        public ClientInfo ClientInfo { get; }

        public void Handshake(ClickHouseConnectionSettings connectionSettings) {
            _connectionSettings = connectionSettings;
            _compressor = connectionSettings.Compress ? Compressor.Create(connectionSettings) : null;
            WriteUInt((int) ClientMessageType.Hello);

            WriteString(string.IsNullOrEmpty(connectionSettings.ClientName) ? ClientInfo.ClientName : connectionSettings.ClientName);
            WriteUInt(ClientInfo.ClientVersionMajor);
            WriteUInt(ClientInfo.ClientVersionMinor);
            WriteUInt(ClientInfo.ClientRevision);
            WriteString(connectionSettings.Database);
            WriteString(connectionSettings.User);
            WriteString(connectionSettings.Password);
            _ioStream.Flush();

            var serverHello = ReadUInt();
            if (serverHello == (int) ServerMessageType.Hello) {
                var serverName = ReadString();
                var serverMajor = ReadUInt();
                var serverMinor = ReadUInt();
                var serverBuild = ReadUInt();
                string serverTz = null, serverDn = null;
                ulong serverPatch = 0;
                if (serverBuild >= ProtocolCaps.DbmsMinRevisionWithServerTimezone)
                    serverTz = ReadString();
                if (serverBuild >= ProtocolCaps.DbmsMinRevisionWithServerDisplayName)
                    serverDn = ReadString();
                if (serverBuild >= ProtocolCaps.DbmsMinRevisionWithServerVersionPatch)
                    serverPatch = (uint) ReadUInt();
                ServerInfo = new ServerInfo {
                    Build = serverBuild,
                    Major = serverMajor,
                    Minor = serverMinor,
                    Name = serverName,
                    Timezone = serverTz,
                    Patch = (long) serverPatch,
                    DisplayName = serverDn
                };
            } else if (serverHello == (int) ServerMessageType.Exception) {
                _owner.MaybeSetBroken(null);
                ReadAndThrowException();
            } else {
                _owner.MaybeSetBroken(null);
                throw new FormatException($"Bad message type {serverHello:X} received from server.");
            }
        }

        internal void RunQuery(string sql,
                               QueryProcessingStage stage,
                               QuerySettings settings,
                               ClientInfo clientInfo,
                               IEnumerable<Block> xtables,
                               bool noData) {
            try
            {
                if (_connectionSettings.Trace)
                    Trace.WriteLine($"Executing sql \"{sql}\"", "ClickHouse.Ado");
                WriteUInt((int)ClientMessageType.Query);
                WriteString("");
                if (ServerInfo.Build >= ProtocolCaps.DbmsMinRevisionWithClientInfo)
                {
                    if (clientInfo == null)
                        clientInfo = ClientInfo;
                    else
                        clientInfo.QueryKind = QueryKind.Secondary;

                    clientInfo.Write(this, _connectionSettings.ClientName);
                }

                var compressionMethod = _compressor != null ? _compressor.Method : CompressionMethod.Lz4;
                if (settings != null)
                {
                    settings.Write(this);
                    compressionMethod = settings.Get<CompressionMethod>("compression_method");
                }
                else
                {
                    WriteString("");
                }

                WriteUInt((int)stage);
                WriteUInt(_connectionSettings.Compress ? (int)compressionMethod : 0);
                WriteString(sql);
                _baseStream.Flush();

                if (ServerInfo.Build >= ProtocolCaps.DbmsMinRevisionWithTemporaryTables && noData)
                {
                    new Block().Write(this);
                    _baseStream.Flush();
                }

                if (ServerInfo.Build >= ProtocolCaps.DbmsMinRevisionWithTemporaryTables) SendBlocks(xtables);
            }
            catch (Exception e)
            {
                _owner.MaybeSetBroken(e);
                throw;
            }
        }

        internal Block ReadSchema() {
            try
            {
                var schema = new Response();
                if (ServerInfo.Build >= ProtocolCaps.DbmsMinRevisionWithColumnDefaultsMetadata)
                {
                    ReadPacket(schema);
                    if (schema.Type == ServerMessageType.TableColumns)
                        ReadPacket(schema);
                }
                else
                {
                    ReadPacket(schema);
                }

                return schema.Blocks.First();
            }
            catch (Exception e)
            {
                _owner.MaybeSetBroken(e);
                throw;
            }
        }

        internal void SendBlocks(IEnumerable<Block> blocks)
        {
            try
            {
                if (blocks != null)
                    foreach (var block in blocks)
                    {
                        block.Write(this);
                        _baseStream.Flush();
                    }

                new Block().Write(this);
                _baseStream.Flush();
            }
            catch (Exception e)
            {
                _owner.MaybeSetBroken(e);
                throw;
            }
        }

        internal Response ReadResponse()
        {
            try
            {
                var rv = new Response();
                while (true)
                {
                    if (!_poll()) continue;
                    if (!ReadPacket(rv)) break;
                }

                return rv;
            }
            catch (Exception e)
            {
                _owner.MaybeSetBroken(e);
                throw;
            }
        }

        internal Block ReadBlock() {
            try
            {
                var rv = new Response();
                while (ReadPacket(rv))
                    if (rv.Blocks.Any())
                        return rv.Blocks.First();
                return null;
            }
            catch (Exception e)
            {
                _owner.MaybeSetBroken(e);
                throw;
            }
        }

        internal bool ReadPacket(Response rv) {
            var type = (ServerMessageType) ReadUInt();
            rv.Type = type;
            switch (type) {
                case ServerMessageType.Data:
                case ServerMessageType.Totals:
                case ServerMessageType.Extremes:
                    rv.AddBlock(Block.Read(this));
                    return true;
                case ServerMessageType.Exception:
                    ReadAndThrowException();
                    return false;
                case ServerMessageType.Progress: {
                    var rows = ReadUInt();
                    var bytes = ReadUInt();
                    long total = 0;
                    if (ServerInfo.Build >= ProtocolCaps.DbmsMinRevisionWithTotalRowsInProgress)
                        total = ReadUInt();
                    rv.OnProgress(rows, total, bytes);
                    return true;
                }
                case ServerMessageType.ProfileInfo: {
                    var rows = ReadUInt();
                    var blocks = ReadUInt();
                    var bytes = ReadUInt();
                    var appliedLimit = ReadUInt(); //bool
                    var rowsNoLimit = ReadUInt();
                    var calcRowsNoLimit = ReadUInt(); //bool
                    return true;
                }
                case ServerMessageType.TableColumns: {
                    var empty = ReadString();
                    var columns = ReadString();
                }
                    return true;
                case ServerMessageType.Pong:
                    return true;
                case ServerMessageType.EndOfStream:
                    rv.OnEnd();
                    return false;
                default:
                    throw new InvalidOperationException($"Received unknown packet type {type} from server.");
            }
        }

        public static string EscapeName(string str) {
            if (!NameRegex.IsMatch(str)) throw new ArgumentException($"'{str}' is invalid identifier.");
            return str;
        }

        public static string EscapeStringValue(string str) => "\'" + str.Replace("\\", "\\\\").Replace("\'", "\\\'") + "\'";

        public void Close() {
            if (_compStream != null)
                _compStream.Dispose();
        }

        public static string UnescapeStringValue(string src) {
            if (src == null) return string.Empty;
            if (src.StartsWith("'") && src.EndsWith("'")) return src.Substring(1, src.Length - 2).Replace("\\'", "'").Replace("\\\\", "\\");
            return src;
        }

        #region Structures IO

        private void ReadAndThrowException() => throw ReadException();

        private Exception ReadException() {
            var code = BitConverter.ToInt32(ReadBytes(4), 0); // reader.ReadInt32();
            var name = ReadString();
            var message = ReadString();
            var stackTrace = ReadString();
            var nested = ReadBytes(1).Any(x => x != 0);
            if (nested)
                return new ClickHouseException(message, ReadException()) {
                    Code = code,
                    Name = name,
                    ServerStackTrace = stackTrace
                };
            return new ClickHouseException(message) {
                Code = code,
                Name = name,
                ServerStackTrace = stackTrace
            };
        }

        #endregion

        #region Low-level IO

        internal void WriteByte(byte b) => _ioStream.Write(new[] {b}, 0, 1);

        internal void WriteUInt(long s) {
            var x = (ulong) s;
            for (var i = 0; i < 9; i++) {
                var b = (byte) ((byte) x & 0x7f);
                if (x > 0x7f)
                    b |= 0x80;
                WriteByte(b);
                x >>= 7;
                if (x == 0) return;
            }
        }

        internal long ReadUInt() {
            var x = 0;
            for (var i = 0; i < 9; ++i) {
                var b = ReadByte();
                x |= (b & 0x7F) << (7 * i);

                if ((b & 0x80) == 0) return x;
            }

            return x;
        }

        internal void WriteString(string s) {
            if (s == null) s = "";
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteUInt((uint) bytes.Length);
            WriteBytes(bytes);
        }

        internal string ReadString() {
            var len = ReadUInt();
            if (len > int.MaxValue)
                throw new ArgumentException("Server sent too long string.");
            var rv = len == 0 ? string.Empty : Encoding.UTF8.GetString(ReadBytes((int) len));
            return rv;
        }

        public byte[] ReadBytes(int i, int size=-1) {
            var bytes = new byte[size == -1 ? i : size];
            var read = 0;

            do {
                var cur = _ioStream.Read(bytes, read, i - read);
                read += cur;

                if (cur == 0) {
                        throw new EndOfStreamException();
                }
            } while (read < i);

            return bytes;
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public void WriteBytes(byte[] bytes) => _ioStream.Write(bytes, 0, bytes.Length);

        public void WriteBytes(byte[] bytes, int offset, int len) => _ioStream.Write(bytes, offset, len);

        #endregion

        #region Compression

        internal class CompressionHelper : IDisposable {
            private readonly ProtocolFormatter _formatter;

            public CompressionHelper(ProtocolFormatter formatter) {
                _formatter = formatter;
                formatter.StartCompression();
            }

            public void Dispose() => _formatter.EndCompression();
        }

        internal class DecompressionHelper : IDisposable {
            private readonly ProtocolFormatter _formatter;

            public DecompressionHelper(ProtocolFormatter formatter) {
                _formatter = formatter;
                formatter.StartDecompression();
            }

            public void Dispose() => _formatter.EndDecompression();
        }

        private void StartCompression() {
            if (_connectionSettings.Compress) {
                Debug.Assert(_compStream == null, "Already doing compression/decompression!");
                _compStream = _compressor.BeginCompression(_baseStream);
                _ioStream = _compStream;
            }
        }

        private void EndCompression() {
            if (_connectionSettings.Compress) {
                Debug.Assert(_compStream != null, "Compression has not been started!");
                _compressor.EndCompression();
                _compStream = null;
                _ioStream = _baseStream;
            }
        }

        private void StartDecompression() {
            if (_connectionSettings.Compress) {
                Debug.Assert(_compStream == null, "Already doing compression/decompression!");
                _compStream = _compressor.BeginDecompression(_baseStream);
                _ioStream = _compStream;
            }
        }

        private void EndDecompression() {
            if (_connectionSettings.Compress) {
                Debug.Assert(_compStream != null, "Compression has not been started!");
                _compressor.EndDecompression();
                _compStream = null;
                _ioStream = _baseStream;
            }
        }

        internal CompressionHelper Compression => new CompressionHelper(this);
        internal DecompressionHelper Decompression => new DecompressionHelper(this);

        #endregion
    }
}
