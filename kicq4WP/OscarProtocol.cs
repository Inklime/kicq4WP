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
        private ushort _flapSequenceNumber = 0;
        private readonly SemaphoreSlim _readLock = new SemaphoreSlim(1, 1);
        public Action<string> StatusUpdater { get; set; }

        private ushort _snacRequestId = 1;

        private static readonly HashSet<ushort> IcqSupportedFamilies = new HashSet<ushort>
{
    0x0001, // Generic
    0x0002, // Location services
    0x0003, // Buddy List management
    0x0004, // Messaging (ICBM)
    0x0009, // Privacy
    0x000B, // Usage stats
    0x0010, // Server-stored buddy icons
    0x0013, // Server Side Information (SSI)
    0x0015, // ICQ-specific extensions
    0x0017  // Authorization/registration
};

        private ushort GetNextRequestID()
        {
            return _snacRequestId++;
        }

        public string UIN { get; private set; }
        public OscarProtocol(string uin, string password)
        {
            if (string.IsNullOrWhiteSpace(uin)) throw new ArgumentNullException(nameof(uin));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentNullException(nameof(password));

            UIN = uin;
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

        private byte[] BuildSnacPayload(ushort family, ushort subtype, ushort flags, uint requestId, List<byte[]> tlvs)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((ushort)family);      // SNAC Family
                writer.Write((ushort)subtype);     // SNAC Subtype
                writer.Write((ushort)flags);       // Flags
                writer.Write((uint)requestId);     // Request ID

                foreach (var tlv in tlvs)
                {
                    writer.Write(tlv);
                }

                return ms.ToArray();
            }
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

        public async Task<bool> DirectAuth(uint statusCode) // РАБОЧИЙ!!!
        {
            try
            {
                Debug.WriteLine("[DirectAuth] Building login TLVs...");

                var tlvs = new List<byte[]>();

                // TLV 0x01 — UIN
                byte[] uinBytes = Encoding.UTF8.GetBytes(_uin);
                tlvs.Add(BuildTlv(0x0001, uinBytes));

                // TLV 0x02 — Roasted password
                byte[] passwordBytes = RoastPassword(_password);
                tlvs.Add(BuildTlv(0x0002, passwordBytes));

                // TLV 0x03 — Client ID: "ICQBasic"
                tlvs.Add(BuildTlv(0x0003, Encoding.UTF8.GetBytes("ICQBasic")));

                // TLV 0x16 — Client ID number = 0x010A
                tlvs.Add(BuildTlv(0x0016, new byte[] { 0x01, 0x0A }));

                // TLV 0x17 — Major version = 0x0014
                tlvs.Add(BuildTlv(0x0017, new byte[] { 0x00, 0x14 }));

                // TLV 0x18 — Minor version = 0x0034
                tlvs.Add(BuildTlv(0x0018, new byte[] { 0x00, 0x34 }));

                // TLV 0x19 — Lesser version = 0x0000
                tlvs.Add(BuildTlv(0x0019, new byte[] { 0x00, 0x00 }));

                // TLV 0x1A — Build number = 0x0BB8
                tlvs.Add(BuildTlv(0x001A, new byte[] { 0x0B, 0xB8 }));

                // TLV 0x14 — Distribution number = 0x0000043D
                tlvs.Add(BuildTlv(0x0014, new byte[] { 0x00, 0x00, 0x04, 0x3D }));

                // TLV 0x0F — Language = "en"
                tlvs.Add(BuildTlv(0x000F, Encoding.UTF8.GetBytes("en")));

                // TLV 0x0E — Country = "us"
                tlvs.Add(BuildTlv(0x000E, Encoding.UTF8.GetBytes("us")));

                // Формируем финальный payload: Protocol version + TLV
                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 }); // Big-endian

                    foreach (var tlv in tlvs)
                        writer.Write(tlv);

                    byte[] flapData = ms.ToArray();
                    Debug.WriteLine($"[DirectAuth] Sending login FLAP on channel 0x01...");
                    StatusUpdater?.Invoke("Отправляю login request...");
                    await SendFlapAsync(0x01, flapData);
                }

                Debug.WriteLine("[DirectAuth] Waiting for login response...");
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(10));

                if (flap?.Type == 0x04)
                {
                    Debug.WriteLine($"[DirectAuth] Got FLAP type 0x04, Length={flap.Data.Length}");
                    return await HandleBosRedirectAsync(flap.Data, 0x00000000);
                }

                Debug.WriteLine("[DirectAuth] Unexpected FLAP or no response.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DirectAuth ERROR] {ex.Message}");
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
                _flapSequenceNumber++;

                byte[] flapHeader = new byte[6];
                flapHeader[0] = 0x2A; // FLAP marker
                flapHeader[1] = channel;
                flapHeader[2] = (byte)(_flapSequenceNumber >> 8); // high byte
                flapHeader[3] = (byte)(_flapSequenceNumber & 0xFF); // low byte
                flapHeader[4] = (byte)(data.Length >> 8);
                flapHeader[5] = (byte)(data.Length & 0xFF);

                byte[] fullPacket = new byte[flapHeader.Length + data.Length];
                System.Buffer.BlockCopy(flapHeader, 0, fullPacket, 0, flapHeader.Length);
                System.Buffer.BlockCopy(data, 0, fullPacket, flapHeader.Length, data.Length);

                Debug.WriteLine($"[SendFlap] Channel: 0x{channel:X2}, Seq: {_flapSequenceNumber}, Length: {data.Length}");
                Debug.WriteLine($"[SendFlap] FULL PACKET: {BitConverter.ToString(fullPacket)}");

                _writer.WriteBytes(fullPacket);
                await _writer.StoreAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SendFlap ERROR] {ex.Message}");
                throw;
            }
        }



        private ushort[] ParseSupportedFamilies(byte[] data)
        {
            int count = (data.Length - 10) / 2;
            ushort[] families = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                families[i] = (ushort)((data[10 + i * 2] << 8) | data[10 + i * 2 + 1]);
            }
            return families;
        }

        private async Task SendServiceVersionsRequestAsync(ushort[] supportedFamilies)
        {
            Debug.WriteLine("[Init] Building Service Versions Request...");

            if (supportedFamilies == null || supportedFamilies.Length == 0)
            {
                Debug.WriteLine("[Init ERROR] Нет доступных семейств от сервера.");
                return;
            }

            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                foreach (var family in supportedFamilies)
                {
                    if (!IcqSupportedFamilies.Contains(family))
                    {
                        Debug.WriteLine($"[Init] Пропущено семейство 0x{family:X4} (не поддерживается ICQ)");
                        continue;
                    }

                    ushort version = GetFamilyVersion(family);
                    writer.Write(SwapUInt16(family));
                    writer.Write(SwapUInt16(version));
                    Debug.WriteLine($"[Init] Семейство 0x{family:X4}, версия 0x{version:X4}");
                }

                byte[] payload = ms.ToArray();

                if (payload.Length == 0)
                {
                    Debug.WriteLine("[Init ERROR] Ни одно семейство не отправлено — пустой SNAC 0x01/0x17");
                    return;
                }

                Debug.WriteLine($"[Init] Service version payload: {BitConverter.ToString(payload)}");

                StatusUpdater?.Invoke("Отправляем запрос версий сервисов...");
                await SendSnacAsync(0x01, 0x17, 0x0000, GetNextRequestID(), payload);
                Debug.WriteLine("[Init] Sent SNAC 0x01/0x17 (Service Versions Request)");
            }
        }



        private ushort GetFamilyVersion(ushort family)
        {
            switch (family)
            {
                case 0x0001: return 0x0001; // Generic service
                case 0x0002: return 0x0001; // Location
                case 0x0003: return 0x0001; // Buddy list
                case 0x0004: return 0x0001; // Messaging
                case 0x0006: return 0x0001; // Invitation
                case 0x0009: return 0x0001; // Privacy
                case 0x000B: return 0x0001; // Stats
                case 0x000C: return 0x0001; // Translation
                case 0x0013: return 0x0001; // SSI
                case 0x0015: return 0x0001; // ICQ extensions
                default: return 0x0001;
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
                writer.Write(SwapUInt16(family));      // SNAC family
                writer.Write(SwapUInt16(subtype));     // SNAC subtype
                writer.Write(SwapUInt16(flags));       // SNAC flags
                writer.Write(SwapUInt16(requestId));   // SNAC request ID

                if (data != null)
                    writer.Write(data);

                byte[] snacPayload = ms.ToArray();

                Debug.WriteLine($"[SendSnac] SNAC 0x{family:X4}/0x{subtype:X4}, RequestID=0x{requestId:X4}"); // НЕ УДАЛЯТЬ!!! ЭТО ДЛЯ ПРОВЕРКИ КАК ОТПРАВЛЯЕТСЯ ПАКЕТ
                Debug.WriteLine("[SendSnac] Payload: " + BitConverter.ToString(snacPayload)); // НЕ УДАЛЯТЬ!!! ЭТО ДЛЯ ПРОВЕРКИ КАК ОТПРАВЛЯЕТСЯ ПАКЕТ

                await SendFlapAsync(0x01, snacPayload);
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
                StatusUpdater?.Invoke("Отправляем клиент готов...");
                await SendSnacAsync(0x01, 0x03, 0x0000, 0x0000, data);
                Debug.WriteLine($"[ClientReady] Sent SNAC 0x01/0x02 (length={data.Length})");
            }
        }


        private async Task WaitForServerFamiliesAsync()
        {
            Debug.WriteLine("[BOS] Waiting for SNAC 0x0001/0x0003 from server...");
            StatusUpdater?.Invoke("Ждем список сервисов...");
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
                    StatusUpdater?.Invoke("Получили список сервисов...");
                    Debug.WriteLine("[BOS] Received supported service families list");
                    var supportedFamilies = ParseSupportedFamilies(flap.Data);
                    await SendServiceVersionsRequestAsync(supportedFamilies);
                }

                Debug.WriteLine($"[BOS] Unexpected SNAC 0x{family:X4}/0x{subtype:X4}, ignoring...");
            }
        }



        private async Task<FlapFrame> ReceiveFlapAsync()
        {
            await _readLock.WaitAsync();

            try
            {
                // Загружаем первые 6 байт (FLAP заголовок)
                uint headerRead = await _reader.LoadAsync(6);
                if (headerRead < 6)
                {
                    throw new Exception("FLAP header too short or connection closed");
                }

                byte flapStart = _reader.ReadByte();
                if (flapStart != 0x2A)
                {
                    throw new Exception("Invalid FLAP header signature");
                }

                byte channel = _reader.ReadByte();
                ushort seq = _reader.ReadUInt16();
                ushort length = _reader.ReadUInt16();

                // Загружаем тело пакета
                uint bodyRead = await _reader.LoadAsync(length);
                if (bodyRead < length)
                {
                    throw new Exception("FLAP body truncated or connection closed");
                }

                byte[] data = new byte[length];
                _reader.ReadBytes(data);

                return new FlapFrame
                {
                    Type = channel,
                    Sequence = seq,
                    Data = data
                };
            }
            finally
            {
                _readLock.Release();
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

        private async Task WaitForServiceVersionsAsync()
        {
            Debug.WriteLine("[Init] Waiting for SNAC 0x01/0x18 (Service Versions Response)");
            StatusUpdater?.Invoke("Ждем версии сервисов...");
            for (int i = 0; i < 10; i++)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (flap == null || flap.Type != 0x02 || flap.Data.Length < 10)
                    continue;

                ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                if (family == 0x0001 && subtype == 0x0018)
                {
                    Debug.WriteLine("[Init] Received SNAC 0x01/0x18 — Service Versions Confirmed");
                    return;
                }
                else
                {
                    Debug.WriteLine($"[Init] Ignoring SNAC 0x{family:X4}/0x{subtype:X4}");
                }
            }

            Debug.WriteLine("[Init] Did not receive SNAC 0x01/0x18");
        }


        private Dictionary<ushort, TLV> ParseTlvs(byte[] data)
        {
            var dict = new Dictionary<ushort, TLV>();
            using (var ms = new MemoryStream(data))
            {
                while (ms.Position + 4 <= ms.Length)
                {
                    // Read type (big-endian)
                    byte[] typeBytes = new byte[2];
                    ms.Read(typeBytes, 0, 2);
                    ushort type = (ushort)((typeBytes[0] << 8) | typeBytes[1]);  // Fixed: added closing parenthesis

                    // Read length (big-endian)
                    byte[] lengthBytes = new byte[2];
                    ms.Read(lengthBytes, 0, 2);
                    ushort length = (ushort)((lengthBytes[0] << 8) | lengthBytes[1]);  // Also fixed same issue here

                    // Verify we have enough data
                    if (ms.Position + length > ms.Length)
                    {
                        Debug.WriteLine($"[ParseTLV ERROR] TLV 0x{type:X4} length {length} exceeds remaining data");
                        break;
                    }

                    // Read value (EXACT bytes, no modification)
                    byte[] value = new byte[length];
                    int bytesRead = ms.Read(value, 0, length);

                    if (bytesRead != length)
                    {
                        Debug.WriteLine($"[ParseTLV ERROR] For TLV 0x{type:X4}, expected {length} bytes, got {bytesRead}");
                        continue;
                    }

                    dict[type] = new TLV(type, value);
                }
            }
            return dict;
        }
        public async Task InitializeOscarSessionAsync(uint statusCode)
        {
            Debug.WriteLine("[Init] Starting OSCAR session initialization...");

            try
            {
                // Получаем SNAC 0x01/0x18 (версии)
                FlapFrame response = await ReceiveSnacWithTimeout(0x0001, 0x0018, TimeSpan.FromSeconds(5));
                if (response == null)
                {
                    throw new TimeoutException("Timeout waiting for SNAC 0x01/0x18 (Service Versions Reply)");
                    Debug.WriteLine("[Init ERROR] Timeout waiting for SNAC 0x01/0x18 (Service Versions Reply)");
                    return;
                }

                Debug.WriteLine("[Init] Received SNAC 0x01/0x18 (Service Versions Reply)");

                // ЭТА СТРОКА — ОБЯЗАТЕЛЬНО после подтверждения версий | я пока что не понимаю надо ли оно тут вообще или нет
                //await SendClientReadyAsync();
                //Debug.WriteLine("[Init] Sent SNAC 0x01/0x03 (Client Ready)");

                // 1. Запрашиваем rate limits
                await SendSnacAsync(0x01, 0x06, 0x0000, GetNextRequestID(), null);
                await SendSnacAsync(0x01, 0x07, 0x0000, GetNextRequestID(), null);
                StatusUpdater?.Invoke("Отправляем запрос Rate limits...");
                // 2. Ждём rate limits (SNAC 01/07)
                var rateInfo = await ReceiveSnacWithTimeout(0x01, 0x07, TimeSpan.FromSeconds(5));
                StatusUpdater?.Invoke("Ждем Rate limits...");
                if (rateInfo == null)
                {
                    throw new TimeoutException("Timeout waiting for SNAC 0x01/0x07 (Rate Info)");
                }

                Debug.WriteLine("[Init] Received SNAC 0x01/0x07 (Rate Info)");

                // 3. Отправляем ack (01/08)
                await SendSnacAsync(0x01, 0x08, 0x0000, GetNextRequestID(), null);
                Debug.WriteLine("[Init] Rate limit negotiation complete");

                // 4. Устанавливаем статус (онлайн и т.д.)
                StatusUpdater?.Invoke("Устанавливаем статус...");
                await SetStatusAsync(statusCode);
                Debug.WriteLine("[Init] OSCAR session initialization complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init ERROR] Exception during OSCAR session init: {ex}");
                throw;
            }
        }



        private async Task<FlapFrame> ReceiveSnacWithTimeout(ushort expectedFamily, ushort expectedSubtype, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var flap = await ReceiveFlapWithTimeout(timeout);
                if (flap != null && flap.Type == 0x02 && flap.Data.Length >= 10)
                {
                    ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                    ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                    if (family == expectedFamily && subtype == expectedSubtype)
                    {
                        return flap;
                    }
                }
            }

            throw new TimeoutException("Timeout waiting for SNAC");
        }



        public async Task<List<string>> GetContactsAsync()
        {
            try
            {
                Debug.WriteLine("[GetContacts] Requesting contact list...");
                StatusUpdater?.Invoke("Отправляем запрос КЛ...");

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
                Debug.WriteLine($"[BOS] Connecting to BOS server: {bosHostPort}");
                StatusUpdater?.Invoke("Подключаемся к BOS...");
                // Парсим хост и порт
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

                // 1. Ждем приветствие от сервера (FLAP 0x01)
                Debug.WriteLine("[BOS] Waiting for server hello (FLAP 0x01)...");
                var hello = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(10));
                if (hello == null || hello.Type != 0x01)
                {
                    Debug.WriteLine("[BOS] No server hello received.");
                    return false;
                }

                // Проверяем данные приветствия (должны быть 00-00-00-01)
                if (hello.Data.Length != 4 ||
                    hello.Data[0] != 0x00 ||
                    hello.Data[1] != 0x00 ||
                    hello.Data[2] != 0x00 ||
                    hello.Data[3] != 0x01)
                {
                    Debug.WriteLine($"[BOS] Invalid hello data: {BitConverter.ToString(hello.Data)}");
                    return false;
                }

                Debug.WriteLine("[BOS] Received valid server hello. Preparing to send cookie...");

                // 2. Формируем полный пакет для отправки:
                // - 4 байта: 00 00 00 01 (версия протокола)
                // - TLV 0x0006 с cookie (256 байт)
                var payload = new List<byte>();
                payload.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x01 });
                payload.AddRange(BuildTlv(0x0006, cookieBytes));
                StatusUpdater?.Invoke("Отправляем cookie...");
                Debug.WriteLine($"[BOS] Sending cookie packet (length: {payload.Count} bytes)");
                Debug.WriteLine($"[BOS] Cookie data: {BitConverter.ToString(cookieBytes.Take(32).ToArray())}...");

                // 3. Отправляем с рандомным sequence number
                ushort sequence = (ushort)new Random().Next(10000, 60000);
                await SendFlapAsync(0x01, payload.ToArray());

                // 4. Ждем ответа от сервера (SNAC 0x0001/0x0003)
                Debug.WriteLine("[BOS] Waiting for server response (SNAC 0x0001/0x0003)...");
                var response = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(15));

                if (response == null)
                {
                    Debug.WriteLine("[BOS] No response from server after sending cookie.");
                    return false;
                }

                Debug.WriteLine($"[BOS] Received response: Type=0x{response.Type:X2}, Length={response.Data.Length}");

                // Проверяем что это SNAC (0x02) с нужными данными
                if (response.Type == 0x02 && response.Data.Length >= 10)
                {
                    ushort family = (ushort)((response.Data[0] << 8) | response.Data[1]);
                    ushort subtype = (ushort)((response.Data[2] << 8) | response.Data[3]);

                    Debug.WriteLine($"[BOS] Received SNAC: 0x{family:X4}/0x{subtype:X4}");

                    if (family == 0x0001 && subtype == 0x0003)
                    {
                        StatusUpdater?.Invoke("Получили список сервисов...");
                        Debug.WriteLine("[BOS] Successfully connected to BOS server and server sent services list!");
                        var supportedFamilies = ParseSupportedFamilies(response.Data);
                        await SendServiceVersionsRequestAsync(supportedFamilies);
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
            StatusUpdater?.Invoke("Получили cookie...");
            Debug.WriteLine("[Redirect] Parsing BOS redirect packet...");
            Debug.WriteLine($"[Redirect] Raw TLV data: {BitConverter.ToString(data)}");

            try
            {
                Dictionary<ushort, TLV> tlvs = ParseTlvs(data);
                TLV bosHostTlv;
                TLV cookieTlv;

                // Verify we have required TLVs
                if (!tlvs.TryGetValue(0x0005, out bosHostTlv) ||
                    !tlvs.TryGetValue(0x0006, out cookieTlv))
                {
                    Debug.WriteLine("[Redirect] Missing required TLVs (0x0005 or 0x0006)");
                    return false;
                }

                // Verify cookie length (should be exactly 256 bytes)
                if (cookieTlv.Value.Length != 256)
                {
                    Debug.WriteLine($"[Redirect] Invalid cookie length: {cookieTlv.Value.Length}, expected 256");
                    return false;
                }

                // Create defensive copy of cookie
                byte[] cookieBytes = new byte[256];
                System.Buffer.BlockCopy(cookieTlv.Value, 0, cookieBytes, 0, 256);

                // Extract BOS host (UTF-8 string)
                string bosHost = Encoding.UTF8.GetString(bosHostTlv.Value, 0, bosHostTlv.Value.Length);
                Debug.WriteLine($"[Redirect] BOS Host: {bosHost}");
                Debug.WriteLine($"[Redirect] Cookie (first 32 bytes): {BitConverter.ToString(cookieBytes, 0, 32)}...");

                return await ConnectToBosAsync(bosHost, cookieBytes, statusCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Redirect ERROR] {ex.Message}");
                return false;
            }
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
            StatusUpdater?.Invoke("Ждем redirect...");
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
                    StatusUpdater?.Invoke("Получили redirect...");
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

        private async Task ReceiveLoopAsync()
        {
            while (true)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(30));
                if (flap == null)
                    continue;

                // Разбор FLAP/SNAC и вызов нужных обработчиков
                await HandleFlapAsync(flap);
            }
        }

        private async Task HandleFlapAsync(FlapFrame flap)
        {
            if (flap.Type != 0x02 || flap.Data.Length < 10)
                return;

            ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
            ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

            if (family == 0x01 && subtype == 0x18)
            {
                Debug.WriteLine("[Init] Received Service Versions Response");
                // Просто логируем, без обработки
                Debug.WriteLine($"[ServiceVersions] Data: {BitConverter.ToString(flap.Data)}");
            }
            // и т.д.
        }

        private async Task HandleServiceVersionsResponse(byte[] data)
        {
            try
            {
                Debug.WriteLine("[HandleServiceVersionsResponse] Processing service versions...");

                if (data.Length < 10)
                {
                    Debug.WriteLine("[HandleServiceVersionsResponse] Invalid data length");
                    return;
                }

                // Здесь можно добавить обработку полученных версий сервисов
                Debug.WriteLine($"[HandleServiceVersionsResponse] Data: {BitConverter.ToString(data)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleServiceVersionsResponse ERROR] {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                Debug.WriteLine("[OscarProtocol] Sending logout SNAC (0x01/0x04)");

                // Отправляем SNAC (Family 0x01, Subtype 0x04) — logout
                await SendSnacAsync(0x01, 0x04, 0x0000, 0x0001, null);
                await Task.Delay(200);

                _writer?.DetachStream();
                _writer?.Dispose();
                _writer = null;

                _reader?.DetachStream();
                _reader?.Dispose();
                _reader = null;

                _socket?.Dispose();
                _socket = null;

                Debug.WriteLine("[OscarProtocol] Disconnected cleanly.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OscarProtocol] Disconnect failed: " + ex.Message);
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

        public class TLV
        {
            public ushort Type { get; }
            public byte[] Value { get; }

            public TLV(ushort type, byte[] value)
            {
                if (value == null)
                    throw new ArgumentNullException("value");

                Type = type;
                Value = value;
            }
        }
    }
}
