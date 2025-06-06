using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;


namespace kicq4WP
{
    public class OscarProtocol : IDisposable
    {
        private StreamSocket _socket;
        private DataWriter _writer;
        private DataReader _reader;
        private byte _sequenceNumber;
        private readonly string _uin;
        private readonly string _password;

        public OscarProtocol(string uin, string password)
        {
            if (string.IsNullOrWhiteSpace(uin)) throw new ArgumentNullException(nameof(uin));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentNullException(nameof(password));

            _uin = uin;
            _password = password;

            Debug.WriteLine($"[OscarProtocol] Created with UIN: {_uin}");
        }

        private async Task ConnectAsync()
        {
            _socket = new StreamSocket();
            var hostName = new HostName("195.66.114.37");
            await _socket.ConnectAsync(hostName, "5190");

            _writer = new DataWriter(_socket.OutputStream);
            _reader = new DataReader(_socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial,
                ByteOrder = ByteOrder.BigEndian
            };
        }

        private byte[] BuildTlv(ushort type, byte[] value)
        {
            using (var ms = new MemoryStream())
            {
                byte[] typeBytes = BitConverter.GetBytes(type);
                byte[] lengthBytes = BitConverter.GetBytes((ushort)value.Length);

                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(typeBytes);
                    Array.Reverse(lengthBytes);
                }

                ms.Write(typeBytes, 0, 2);
                ms.Write(lengthBytes, 0, 2);
                ms.Write(value, 0, value.Length);

                return ms.ToArray();
            }
        }

        public async Task<bool> AuthenticateAsync(uint statusCode)
        {
            Debug.WriteLine("[Auth] Starting authentication...");

            try
            {
                await ConnectAsync();

                await SendFlapAsync(0x01, new byte[] { 0x00, 0x00, 0x00, 0x01 });

                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (response == null)
                {
                    Debug.WriteLine("[Auth] No response from server");
                    return false;
                }

                Debug.WriteLine($"[FLAP] Type: {response.Type}, Length: {response.Data.Length}, Data: {BitConverter.ToString(response.Data)}");

                if (response.Type != 0x01)
                {
                    Debug.WriteLine("[Auth] Invalid FLAP response type");
                    return false;
                }

                if (response.Data.Length == 4 &&
                    response.Data[0] == 0x00 &&
                    response.Data[1] == 0x00 &&
                    response.Data[2] == 0x00 &&
                    response.Data[3] == 0x01)
                {
                    Debug.WriteLine("[Auth] Using DirectAuth method");
                    return await DirectAuth(statusCode);
                }

                if (response.Data.Length > 0)
                {
                    var tlvs = ParseTlvs(response.Data);
                    TLV challengeTlv;
                    if (tlvs.TryGetValue(0x0006, out challengeTlv))
                    {
                        Debug.WriteLine("[Auth] Using ChallengeAuth method");
                        return await SendLoginWithChallenge(challengeTlv.Value);
                    }
                }

                Debug.WriteLine("[Auth] No valid auth method detected");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Auth ERROR] {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DirectAuth(uint statusCode)
        {
            try
            {
                Debug.WriteLine("[DirectAuth] Building login TLVs...");
                List<byte> payload = new List<byte>();

                // FLAP protocol version
                payload.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });

                // TLV 0x01 — UIN
                string cleanUin = _uin.TrimEnd('\0');
                byte[] uinBytes = Encoding.UTF8.GetBytes(cleanUin);
                payload.AddRange(BuildTlv(0x01, uinBytes));
                Debug.WriteLine("[DirectAuth] UIN: " + cleanUin);

                // TLV 0x02 — Roasted password
                byte[] passwordBytes = Encoding.UTF8.GetBytes(_password);
                byte[] roasted = RoastPassword(_password); // password — строка
                Debug.WriteLine("[DirectAuth] Roasted password: " + BitConverter.ToString(roasted));
                payload.AddRange(BuildTlv(0x02, roasted));

                // TLV 0x03 — Client ID string
                payload.AddRange(BuildTlv(0x03, Encoding.UTF8.GetBytes("ICQBasic")));

                // TLV 0x16..0x14 — версия клиента
                payload.AddRange(BuildTlv(0x16, new byte[] { 0x01, 0x0A })); // Client ID
                payload.AddRange(BuildTlv(0x17, new byte[] { 0x00, 0x14 })); // Major
                payload.AddRange(BuildTlv(0x18, new byte[] { 0x00, 0x34 })); // Minor
                payload.AddRange(BuildTlv(0x19, new byte[] { 0x00, 0x00 })); // Lesser
                payload.AddRange(BuildTlv(0x1A, new byte[] { 0x0B, 0xB8 })); // Build
                payload.AddRange(BuildTlv(0x14, new byte[] { 0x00, 0x00, 0x04, 0x3D })); // Distribution

                // TLV 0x0F, 0x0E — Язык и страна
                payload.AddRange(BuildTlv(0x0F, Encoding.UTF8.GetBytes("en")));
                payload.AddRange(BuildTlv(0x0E, Encoding.UTF8.GetBytes("us")));

                Debug.WriteLine("[DirectAuth] Sending login FLAP on channel 0x01...");
                await SendFlapAsync(0x01, payload.ToArray());

                Debug.WriteLine("[DirectAuth] Waiting for login response...");
                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));

                if (response != null && response.Type == 0x04)
                {
                    Debug.WriteLine($"[DirectAuth] Got FLAP type 0x04, Length={response.Data.Length}");

                    bool redirectOk = await HandleBosRedirectAsync(response.Data, statusCode);
                    if (!redirectOk)
                    {
                        Debug.WriteLine("[DirectAuth] Failed to handle BOS redirect.");
                        return false;
                    }

                    await InitializeOscarSessionAsync(statusCode);
                    await SetStatusAsync(statusCode);

                    Debug.WriteLine("[DirectAuth] Bingo! Login and BOS session established successfully!");
                    return true;
                }
                else if (response != null)
                {
                    Debug.WriteLine($"[DirectAuth] Unexpected FLAP type: 0x{response.Type:X2}, Length={response.Data.Length}");
                    return false;
                }
                else
                {
                    Debug.WriteLine("[DirectAuth] No response from server.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DirectAuth ERROR] " + ex.Message);
                return false;
            }
        }



        private async Task<bool> SendLoginWithChallenge(byte[] challenge) // TODO: безопасный логин
        {
            try
            {
                Debug.WriteLine($"[ChallengeAuth] Challenge: {BitConverter.ToString(challenge)}");

                byte[] pwBytes = Encoding.UTF8.GetBytes(_password);
                byte[] toHash = new byte[challenge.Length + 1 + pwBytes.Length];

                System.Buffer.BlockCopy(challenge, 0, toHash, 0, challenge.Length);
                toHash[challenge.Length] = 0x00;
                System.Buffer.BlockCopy(pwBytes, 0, toHash, challenge.Length + 1, pwBytes.Length);

                var alg = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
                byte[] hash = alg.HashData(CryptographicBuffer.CreateFromByteArray(toHash)).ToArray();
                Debug.WriteLine($"[ChallengeAuth] MD5 Hash: {BitConverter.ToString(hash)}");

                var tlvs = new List<TLV>
                {
                    new TLV(0x0001, Encoding.UTF8.GetBytes(_uin)),
                    new TLV(0x0002, hash),
                    new TLV(0x0003, new byte[] {0x00, 0x00, 0x00, 0x01}),
                    new TLV(0x0016, new byte[] {0x01, 0x0A}),
                    new TLV(0x0017, new byte[] {0x00, 0x14}),
                    new TLV(0x0018, new byte[] {0x00, 0x34}),
                    new TLV(0x0019, new byte[] {0x00, 0x00}),
                    new TLV(0x001A, new byte[] {0x0B, 0xB8}),
                    new TLV(0x0014, new byte[] {0x00, 0x00, 0x04, 0x3D}),
                    new TLV(0x000F, Encoding.UTF8.GetBytes("en")),
                    new TLV(0x000E, Encoding.UTF8.GetBytes("us")),
                    new TLV(0x0002, Encoding.UTF8.GetBytes("QIP user"))
                };

                await SendTlvLogin(tlvs);
                await Task.Delay(300);

                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                return response != null && response.Type == 0x02;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChallengeAuth ERROR] {ex.Message}");
                return false;
            }
        }

        private async Task SendTlvLogin(List<TLV> tlvs)
        {
            byte[] tlvPayload = BuildTlvPayload(tlvs);
            Debug.WriteLine("[SendTlvLogin] TLV Payload: " + BitConverter.ToString(tlvPayload));

            byte[] flap = BuildFlapFrame(0x01, tlvPayload);
            Debug.WriteLine("[SendTlvLogin] Full FLAP Frame: " + BitConverter.ToString(flap));

            try
            {
                _writer.WriteBytes(flap);
                await _writer.StoreAsync();
                Debug.WriteLine("[SendTlvLogin] Frame sent successfully (" + flap.Length + " bytes)");

                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SendTlvLogin ERROR] " + ex.Message);
                throw;
            }
        }

        private byte[] BuildTlvPayload(List<TLV> tlvs)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var tlv in tlvs)
                {
                    byte[] typeBytes = BitConverter.GetBytes((ushort)tlv.Type);
                    byte[] lengthBytes = BitConverter.GetBytes((ushort)tlv.Value.Length);

                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(typeBytes);
                        Array.Reverse(lengthBytes);
                    }

                    ms.Write(typeBytes, 0, 2);
                    ms.Write(lengthBytes, 0, 2);
                    ms.Write(tlv.Value, 0, tlv.Value.Length);
                }

                return ms.ToArray();
            }
        }

        private byte[] RoastPassword(string password)
        {
            byte[] key = new byte[] { 0xF3, 0x26, 0x81, 0xC4, 0x39, 0x86, 0xDB, 0x92,
                              0x71, 0xA3, 0xB9, 0xE6, 0x53, 0x7A, 0x95, 0x7C };

            byte[] input = Encoding.UTF8.GetBytes(password);
            byte[] roasted = new byte[input.Length];

            for (int i = 0; i < input.Length; i++)
            {
                roasted[i] = (byte)(input[i] ^ key[i % key.Length]);
            }

            return roasted;
        }



        private byte[] BuildFlapFrame(byte channel, byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x2A);
                ms.WriteByte(channel);
                ms.WriteByte(0x00);
                ms.WriteByte(_sequenceNumber++);
                ms.WriteByte((byte)(data.Length >> 8));
                ms.WriteByte((byte)(data.Length & 0xFF));
                ms.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private async Task SendFlapAsync(byte channel, byte[] data)
        {
            try
            {
                byte[] flap = BuildFlapFrame(channel, data);

                Debug.WriteLine($"[SendFlap] Channel: 0x{channel:X2}, Data length: {data.Length}");
                Debug.WriteLine($"[SendFlap] Data: {BitConverter.ToString(data)}");
                Debug.WriteLine($"[SendFlap] Full FLAP frame: {BitConverter.ToString(flap)}");

                if (_writer == null)
                {
                    throw new InvalidOperationException("DataWriter not initialized");
                }

                _writer.WriteBytes(flap);
                await _writer.StoreAsync();
                await Task.Delay(300);

                Debug.WriteLine($"[SendFlap] Successfully sent {flap.Length} bytes");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SendFlap ERROR] {ex.Message}");
                throw;
            }
        }

        private ushort SwapUInt16(ushort value)
        {
            return (ushort)((value >> 8) | (value << 8));
        }

        private async Task SendSnacAsync(ushort family, ushort subtype, ushort flags, ushort requestId, byte[] data)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(SwapUInt16(family));
                writer.Write(SwapUInt16(subtype));
                writer.Write(SwapUInt16(flags));
                writer.Write(SwapUInt16(requestId));

                if (data != null)
                    writer.Write(data);

                byte[] snacPayload = ms.ToArray();
                await SendFlapAsync(0x02, snacPayload);
            }
        }


       private async Task SendClientReadyAsync()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // 9 пар (Family ID + Version)
                ushort[,] families = new ushort[,]
                {
            {0x0001, 0x0003}, // Generic
            //{0x0002, 0x0001}, // Location
            //{0x0003, 0x0001}, // Buddy List
            {0x0004, 0x0001}, // Messaging
            {0x0006, 0x0001}, // Chat
            {0x0008, 0x0001}, // BOS
            {0x0009, 0x0001}, // User Lookup
            {0x000A, 0x0001}, // Stats
            {0x000B, 0x0001}, // Translation
            {0x0013, 0x0001}, // SSI
            {0x0015, 0x0001}, // ICQ Extensions
                };

                for (int i = 0; i < families.GetLength(0); i++)
                {
                    writer.Write(SwapUInt16(families[i, 0]));
                    writer.Write(SwapUInt16(families[i, 1]));
                }

                byte[] data = ms.ToArray();

                Debug.WriteLine($"[ClientReady] Payload bytes: {BitConverter.ToString(data)}");
                Debug.WriteLine($"[ClientReady] Total length: {data.Length} bytes");

                await SendSnacAsync(0x01, 0x03, 0x0000, 0x0000, data);
                Debug.WriteLine($"[ClientReady] Sent SNAC 0x01/0x02 (length={data.Length})");
            }
        }


        private async Task WaitForServerFamiliesAsync()
        {
            Debug.WriteLine("[BOS] Waiting for SNAC 0x0001/0x0003 from server...");

            while (true)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (flap == null || flap.Type != 0x02 || flap.Data.Length < 10)
                {
                    Debug.WriteLine("[BOS] Invalid or empty FLAP");
                    continue;
                }

                ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                if (family == 0x0001 && subtype == 0x0003)
                {
                    Debug.WriteLine("[BOS] Received SNAC 0x0001/0x0003 — server families OK");
                    break;
                }

                Debug.WriteLine($"[BOS] Unexpected SNAC 0x{family:X4}/0x{subtype:X4}, ignoring...");
            }
        }



        private async Task<FlapFrame> ReceiveFlapAsync()
        {
            const int headerSize = 6;

            try
            {
                _reader.InputStreamOptions = InputStreamOptions.Partial;
                _reader.ByteOrder = ByteOrder.BigEndian;

                uint loadedHeader = await _reader.LoadAsync(headerSize);
                if (loadedHeader < headerSize)
                {
                    if (_reader.UnconsumedBufferLength == 0 && loadedHeader == 0)
                    {
                        Debug.WriteLine("[ReceiveFlap] Connection closed by server");
                        return null;
                    }
                    throw new Exception($"[ReceiveFlap] FLAP header too short: {loadedHeader} bytes");
                }

                byte marker = _reader.ReadByte();
                if (marker != 0x2A)
                {
                    throw new ProtocolViolationException($"Invalid FLAP signature: 0x{marker:X2}");
                }

                var frame = new FlapFrame
                {
                    Type = _reader.ReadByte(),
                    Sequence = _reader.ReadUInt16(),
                    DataLength = _reader.ReadUInt16()
                };

                Debug.WriteLine($"[ReceiveFlap] Type: 0x{frame.Type:X2}, Seq: {frame.Sequence}, Length: {frame.DataLength}");

                if (frame.DataLength > 0)
                {
                    uint loadedBody = await _reader.LoadAsync(frame.DataLength);
                    if (loadedBody < frame.DataLength)
                    {
                        throw new Exception($"[ReceiveFlap] Expected {frame.DataLength} bytes, got {loadedBody}");
                    }

                    frame.Data = new byte[frame.DataLength];
                    _reader.ReadBytes(frame.Data);

                    Debug.WriteLine($"[ReceiveFlap] Data: {BitConverter.ToString(frame.Data)}");
                }
                else
                {
                    frame.Data = new byte[0];
                }

                return frame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReceiveFlap ERROR] {ex.Message}");
                throw;
            }
        }

        private async Task<FlapFrame> ReceiveFlapWithTimeout(TimeSpan timeout)
        {
            var task = ReceiveFlapAsync();
            if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            {
                return await task;
            }
            else
            {
                Debug.WriteLine("[Timeout] No response from server");
                return null;
            }
        }

        private Dictionary<ushort, TLV> ParseTlvs(byte[] data)
        {
            var dict = new Dictionary<ushort, TLV>();
            using (var ms = new MemoryStream(data))
            {
                while (ms.Position + 4 <= ms.Length)
                {
                    byte[] typeBytes = new byte[2];
                    byte[] lengthBytes = new byte[2];

                    ms.Read(typeBytes, 0, 2);
                    ms.Read(lengthBytes, 0, 2);

                    ushort type = (ushort)((typeBytes[0] << 8) | typeBytes[1]);
                    ushort length = (ushort)((lengthBytes[0] << 8) | lengthBytes[1]);

                    Debug.WriteLine($"[ParseTLV] Type: 0x{type:X4}, Length: {length}");

                    if (ms.Position + length > ms.Length)
                    {
                        Debug.WriteLine($"[ParseTLV ERROR] TLV length {length} exceeds remaining data at position {ms.Position}");
                        throw new System.Net.ProtocolViolationException($"TLV length {length} exceeds remaining data");
                    }

                    byte[] value = new byte[length];
                    int bytesRead = ms.Read(value, 0, length);

                    if (bytesRead != length)
                    {
                        Debug.WriteLine($"[ParseTLV ERROR] Expected {length} bytes, read {bytesRead}");
                        throw new System.Net.ProtocolViolationException($"Failed to read complete TLV value");
                    }

                    Debug.WriteLine($"[ParseTLV] TLV 0x{type:X4}: {BitConverter.ToString(value)}");
                    dict[type] = new TLV(type, value);
                }
            }
            return dict;
        }

        public async Task InitializeOscarSessionAsync(uint statusCode)
        {
            Debug.WriteLine("[Init] Starting OSCAR session initialization...");

            // 1. Client Ready — MUST go first
            //await SendClientReadyAsync();
            //Debug.WriteLine("[Init] Sent ClientReady");


            // 2. Wait for Server Supported Families: SNAC 0x01/0x03
            for (int i = 0; i < 10; i++)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(2));
                if (flap == null || flap.Type != 0x02 || flap.Data.Length < 10)
                    continue;

                ushort fam = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                ushort sub = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                Debug.WriteLine($"[FAM] SNAC 0x{fam:X4}/0x{sub:X4}");

                if (fam == 0x0001 && sub == 0x0003)
                {
                    Debug.WriteLine("[Init] Server sent supported families — continuing init");
                    break;
                }
            }

            // 3. Capabilities (TLV 0x0006 + 0x000C)
            await SendCapabilitiesAsync();
            Debug.WriteLine("[Init] Sent Capabilities");

            // 4. Now that server accepted, continue init

            // 4.1 — Set your online status
            await SetStatusAsync(statusCode);

            // 4.2 — Request rate info
            await SendSnacAsync(0x01, 0x17, 0x0000, 0x0000, new byte[0]);

            // 4.3 — Request service versions
            await SendSnacAsync(0x09, 0x02, 0x0000, 0x0000, new byte[0]);

            // 4.4 — Request rights for location service
            await SendSnacAsync(0x01, 0x08, 0x0000, 0x0000, new byte[0]);

            // 4.5 — Request buddy list rights
            await SendSnacAsync(0x02, 0x02, 0x0000, 0x0000, new byte[0]);

            // 4.6 — Now load contact list
            await GetContactsAsync();

            Debug.WriteLine("[Init] OSCAR session initialization complete.");
        }

        


        public async Task<List<string>> GetContactsAsync()
        {
            try
            {
                Debug.WriteLine("[GetContacts] Requesting contact list...");

                byte[] snac = {
                    0x00, 0x13,
                    0x04, 0x00,
                    0x00, 0x00, 0x00, 0x01
                };

                await SendFlapAsync(0x02, snac);
                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));

                if (response == null || response.Type != 0x02 || response.Data.Length == 0)
                {
                    throw new System.Net.ProtocolViolationException("Invalid contact list response");
                }

                return new List<string> { "123456", "654321" };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetContacts ERROR] {ex.Message}");
                return new List<string>();
            }
        }

        private async Task<bool> ConnectToBosAsync(string bosHostPort, byte[] cookieBytes, uint statusCode)
        {
            try
            {
                Debug.WriteLine("[BOS] Connecting to BOS server " + bosHostPort);

                string[] parts = bosHostPort.Split(':');
                string host = parts[0];
                string port = parts.Length > 1 ? parts[1] : "5190";

                // Закрываем предыдущее соединение
                _socket?.Dispose();

                // Создаем новое соединение
                _socket = new StreamSocket();
                await _socket.ConnectAsync(new HostName(host), port);

                _writer = new DataWriter(_socket.OutputStream);
                _reader = new DataReader(_socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial,
                    ByteOrder = ByteOrder.BigEndian
                };

                Debug.WriteLine($"[BOS] Cookie length: {cookieBytes.Length}");
                Debug.WriteLine($"[BOS] Cookie (hex): {BitConverter.ToString(cookieBytes)}");

                Debug.WriteLine("[BOS] Waiting for server hello (FLAP 0x01)...");
                var hello = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(10));
                if (hello == null || hello.Type != 0x01)
                {
                    Debug.WriteLine("[BOS] No server hello received.");
                    return false;
                }

                Debug.WriteLine("[BOS] Received server hello. Sending cli_cookie...");
                Debug.WriteLine($"[BOS] Sending cookie: {BitConverter.ToString(cookieBytes)}");

                await SendFlapAsync(0x01, cookieBytes);

                Debug.WriteLine("[BOS] Cookie sent. Waiting for response...");

                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(15));

                if (response == null)
                {
                    Debug.WriteLine("[BOS] No response from server after sending cookie.");
                    return false;
                }

                Debug.WriteLine($"[BOS] Received response: Type={response.Type}, Length={response.Data.Length}");

                if (response.Type == 0x02 && response.Data.Length >= 10)
                {
                    ushort family = (ushort)((response.Data[0] << 8) | response.Data[1]);
                    ushort subtype = (ushort)((response.Data[2] << 8) | response.Data[3]);

                    Debug.WriteLine($"[BOS] Received SNAC: 0x{family:X4}/0x{subtype:X4}");

                    if (family == 0x0001 && subtype == 0x0003)
                    {
                        Debug.WriteLine("[BOS] Successfully connected to BOS server!");
                        return true;
                    }
                }

                Debug.WriteLine("[BOS] Unexpected response from server.");
                Debug.WriteLine($"[BOS] Response data: {BitConverter.ToString(response.Data)}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BOS ERROR] {ex.Message}");
                return false;
            }
        }

        private async Task<bool> HandleBosRedirectAsync(byte[] data, uint statusCode)
        {
            Debug.WriteLine("[Redirect] Parsing BOS redirect packet...");
            Debug.WriteLine($"[Redirect] Raw TLV data: {BitConverter.ToString(data)}");

            var tlvs = ParseTlvs(data);

            if (!tlvs.ContainsKey(0x0005) || !tlvs.ContainsKey(0x0006))
            {
                Debug.WriteLine("[Redirect] Required TLVs not found.");
                return false;
            }

            var bosHostTlv = tlvs[0x0005];
            var cookieTlv = tlvs[0x0006];

            // BOS host - это строка UTF-8
            string bosHost = Encoding.UTF8.GetString(bosHostTlv.Value, 0, bosHostTlv.Value.Length);

            // Cookie - бинарные данные, не трогаем!
            byte[] cookieBytes = cookieTlv.Value;

            Debug.WriteLine($"[Redirect] BOS Host: {bosHost}");
            Debug.WriteLine($"[Redirect] Cookie TLV length: {cookieTlv.Value.Length}");
            Debug.WriteLine($"[Redirect] Cookie (hex): {BitConverter.ToString(cookieBytes)}");

            if (await ConnectToBosAsync(bosHost, cookieBytes, statusCode))
            {
                Debug.WriteLine("[Redirect] BOS connect successful.");
                await InitializeOscarSessionAsync(statusCode);
                await SetStatusAsync(statusCode);
                return true;
            }

            Debug.WriteLine("[Redirect] Failed to connect to BOS server.");
            return false;
        }


        private byte[] TrimNullBytes(byte[] input)
        {
            int start = 0;
            while (start < input.Length && input[start] == 0)
                start++;

            int end = input.Length - 1;
            while (end >= 0 && input[end] == 0)
                end--;

            if (start > end)
                return new byte[0];

            byte[] result = new byte[end - start + 1];
            System.Buffer.BlockCopy(input, start, result, 0, result.Length);
            return result;
        }


        private async Task<bool> WaitForRedirectOrBosAsync(uint statusCode)
        {
            Debug.WriteLine("[Login] Waiting for redirect or BOS connect...");

            while (true)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (flap == null)
                {
                    Debug.WriteLine("[Login] No FLAP response");
                    return false;
                }

                if (flap.Type == 0x04)
                {
                    Debug.WriteLine($"[Login] Got redirect FLAP (0x04), Length: {flap.Data.Length}");
                    return await HandleBosRedirectAsync(flap.Data, statusCode);
                }

                Debug.WriteLine($"[Login] Ignoring unexpected FLAP type: 0x{flap.Type:X2}");
            }
        }




        public async Task<bool> AuthenticateAndInitializeAsync(string nickname, uint statusCode)
        {
            if (!await AuthenticateAsync(statusCode))
                return false;

            if (!await WaitForRedirectOrBosAsync(statusCode))
                return false;

            await InitializeOscarSessionAsync(statusCode);
            return true;
        }

        public async Task SendCapabilitiesAsync()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // TLV 0x0006: user class/status
                writer.Write(SwapUInt16(0x0006)); // TLV type
                writer.Write(SwapUInt16(0x0004)); // Length
                writer.Write(SwapUInt32(0x00000000)); // Online + Normal class

                // TLV 0x000C: Capabilities block (GUIDs)
                byte[] caps = new byte[]
                {
            // Standard ICQ client capabilities
            0x09, 0x46, 0x13, 0x4C, 0x4B, 0xE2, 0x4C, 0x7F,
            0xBB, 0xF8, 0x3F, 0xC3, 0xD6, 0xE7, 0x09, 0x32 // Basic messaging
                };

                writer.Write(SwapUInt16(0x000C));
                writer.Write(SwapUInt16((ushort)caps.Length));
                writer.Write(caps);

                byte[] data = ms.ToArray();
                await SendSnacAsync(0x0001, 0x000E, 0x0000, 0x0000, data);

                Debug.WriteLine("[Capabilities] Sent user info with basic capability block");
            }
        }


        public async Task SendMessageAsync(string uin, string message)
        {
            try
            {
                Debug.WriteLine($"[SendMessage] To {uin}: {message}");
                byte[] msgBytes = Encoding.UTF8.GetBytes(message);
                Debug.WriteLine($"[SendMessage] Message: {BitConverter.ToString(msgBytes)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SendMessage ERROR] {ex.Message}");
            }
        }

        public async Task SetStatusAsync(uint statusCode)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(SwapUInt16(0x0006));
                writer.Write(SwapUInt16(0x0004));
                writer.Write(SwapUInt32(statusCode));

                byte[] tlvData = ms.ToArray();
                await SendSnacAsync(0x0001, 0x000e, 0x0000, 0x0000, tlvData);

                Debug.WriteLine($"[SetStatus] Sent status: 0x{statusCode:X8}");
            }
        }



        private uint SwapUInt32(uint value)
        {
            return ((value & 0x000000FFU) << 24) |
                   ((value & 0x0000FF00U) << 8) |
                   ((value & 0x00FF0000U) >> 8) |
                   ((value & 0xFF000000U) >> 24);
        }


        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _socket?.Dispose();
        }

        public async Task ReceiveServerSnacsAsync()
        {
            Debug.WriteLine("[SnacReceiver] Listening for server SNACs...");

            for (int i = 0; i < 10; i++) // ограничим 10 пакетами для теста
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(2));
                if (flap == null || flap.Type != 0x02 || flap.Data.Length < 10)
                    continue;

                ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                Debug.WriteLine($"[SNAC] Server responded: 0x{family:X4}/0x{subtype:X4}, Data: {BitConverter.ToString(flap.Data)}");
            }
        }

        private byte[] GetPseudoAsciiBytes(string input)
        {
            byte[] result = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                result[i] = (byte)(c <= 0x7F ? c : '?');
            }
            return result;
        }


        private class FlapFrame
        {
            public byte Type { get; set; }
            public ushort Sequence { get; set; }
            public ushort DataLength { get; set; }
            public byte[] Data { get; set; }
        }

        private class TLV
        {
            public ushort Type { get; }
            public byte[] Value { get; }

            public TLV(ushort type, byte[] value)
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }

                Type = type;
                Value = value;
            }
        }
    }
}
