// Note: this implementation is only compatible with kicq server (kicq.ru or 195.66.114.37) use the file AT YOUR OWN RISK!

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;
using Windows.UI.Popups;
using System.Collections.ObjectModel;
using Windows.UI.Core;

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
        // SNAC service handlers
        private CancellationTokenSource _receiveCts;
        private ObservableCollection<Contact> contacts;
        private Dictionary<string, List<string[]>> _pendingMessages =
        new Dictionary<string, List<string[]>>();
        public Action<string> StatusUpdater { get; set; }
        public event Action<string, string> IncomingMessage;

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
        private Task _;

        public ushort GetNextRequestID()
        {
            return _snacRequestId++;
        }

        public string UIN { get; private set; }
        public CoreDispatcher _dispatcher { get; private set; }

        public OscarProtocol(string uin, string password, CoreDispatcher dispatcher)
        {
            if (string.IsNullOrWhiteSpace(uin)) throw new ArgumentNullException(nameof(uin));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentNullException(nameof(password));

            UIN = uin;
            _uin = uin;
            _password = password;
            _dispatcher = dispatcher;

            Debug.WriteLine($"[OscarProtocol] Created with UIN: {_uin}");
        }

        internal string GetContactStatus(string uin)
        {
            throw new NotImplementedException();
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

        internal Task<DateTime> GetLastOnlineTimeAsync(string uin)
        {
            throw new NotImplementedException();
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

                Debug.WriteLine($"[FLAP] Type: {response.Channel}, Length: {response.Data.Length}, Data: {BitConverter.ToString(response.Data)}");

                if (response.Channel != 0x01)
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

                if (flap?.Channel == 0x04)
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




        private async Task<bool> SendLoginWithChallenge(byte[] challenge) //(нерабочий) TODO: безопасный логин
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
                return response != null && response.Channel == 0x02;
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
                if (data == null) data = new byte[0];
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
                int count = 0;

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
                    count++;
                }

                if (count == 0)
                {
                    Debug.WriteLine("[Init ERROR] Нет ICQ-совместимых семейств для отправки.");
                    return;
                }

                byte[] payload = ms.ToArray();
                Debug.WriteLine($"[Init] Service version payload: {BitConverter.ToString(payload)}");

                StatusUpdater?.Invoke("Отправляем запрос версий сервисов...");

                ushort requestId = GetNextRequestID();
                await SendSnacAsync(0x01, 0x17, 0x0000, requestId, payload);

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

        public async Task SendSnacAsync(ushort family, ushort subtype, ushort flags, uint requestId, byte[] data)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(SwapUInt16(family));       // SNAC family
                writer.Write(SwapUInt16(subtype));      // SNAC subtype
                writer.Write(SwapUInt16(flags));        // SNAC flags
                writer.Write(SwapUInt32(requestId));    // SNAC request ID (исправлено)

                if (data != null)
                    writer.Write(data);

                byte[] snacPayload = ms.ToArray();

                Debug.WriteLine($"[SendSnac] SNAC 0x{family:X4}/0x{subtype:X4}, RequestID=0x{requestId:X4}");
                Debug.WriteLine("[SendSnac] Payload: " + BitConverter.ToString(snacPayload));

                await SendFlapAsync(0x02, snacPayload); // Channel 0x02
            }
        }




        private async Task SendClientReadyAsync()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                uint requestId = GetNextRequestID();

                // Точно по дампу из документации
                ushort[][] families = new ushort[][]
                {
            new ushort[] { 0x0001, 0x0003, 0x0110, 0x047B }, // Generic
            new ushort[] { 0x0013, 0x0002, 0x0110, 0x047B }, // SSI
            new ushort[] { 0x0002, 0x0001, 0x0101, 0x047B }, // Location
            new ushort[] { 0x0003, 0x0001, 0x0110, 0x047B }, // Buddy list
            new ushort[] { 0x0015, 0x0001, 0x0110, 0x047B }, // ICQ extensions
            new ushort[] { 0x0004, 0x0001, 0x0110, 0x047B }, // Messaging
            new ushort[] { 0x0006, 0x0001, 0x0110, 0x047B }, // BOS
            new ushort[] { 0x0009, 0x0001, 0x0110, 0x047B }, // Privacy
            new ushort[] { 0x000A, 0x0001, 0x0110, 0x047B }, // Search
            new ushort[] { 0x000B, 0x0001, 0x0110, 0x047B }, // Stats
                };

                foreach (var fam in families)
                {
                    writer.Write(SwapUInt16(fam[0]));
                    writer.Write(SwapUInt16(fam[1]));
                    writer.Write(SwapUInt16(fam[2]));
                    writer.Write(SwapUInt16(fam[3]));
                }

                await SendSnacAsync(0x01, 0x02, 0x0000, requestId, ms.ToArray());
                Debug.WriteLine("[ClientReady] Sent SNAC(01,02)");
            }
        }



        private async Task WaitForServerFamiliesAsync()
        {
            Debug.WriteLine("[BOS] Waiting for SNAC 0x0001/0x0003 from server...");
            StatusUpdater?.Invoke("Ждем список сервисов...");
            while (true)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (flap == null || flap.Channel != 0x02 || flap.Data.Length < 10)
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
                uint headerRead = await _reader.LoadAsync(6);
                if (headerRead < 6)
                    throw new Exception("Connection closed by remote host");

                byte[] header = new byte[6];
                _reader.ReadBytes(header);

                var flap = FlapFrame.Parse(header);
                if (flap == null || flap.StartMarker != 0x2A)
                    throw new Exception("Invalid FLAP header");

                uint dataRead = await _reader.LoadAsync(flap.DataLength);
                if (dataRead < flap.DataLength)
                    throw new Exception("Connection closed during data read");

                flap.Data = new byte[flap.DataLength];
                _reader.ReadBytes(flap.Data);

                Debug.WriteLine("[FLAP] Received: Channel=0x" + flap.Channel.ToString("X2") +
                                ", Seq=" + flap.Sequence + ", Length=" + flap.DataLength);
                return flap;
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
                if (flap == null || flap.Channel != 0x02 || flap.Data.Length < 10)
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
                var response = await ReceiveSnacWithTimeout(0x0001, 0x0018, TimeSpan.FromSeconds(5));
                if (response == null)
                {
                    Debug.WriteLine("[Init ERROR] Timeout waiting for SNAC 0x01/0x18");
                    return;
                }
                Debug.WriteLine("[Init] Received SNAC 0x01/0x18");

                // Отправляем все запросы и получаем контакты
                await InitServicesAsync();
                await Task.Delay(200);

                // SNAC(02,04) — capabilities
                await SendClientCapabilitiesAsync();
                await Task.Delay(200);

                // SNAC(01,1E) — статус
                await SendSetStatusAsync(statusCode);
                await Task.Delay(200);

                // SNAC(01,02) — ClientReady
                await SendClientReadyAsync();
                await Task.Delay(200);

                // SNAC(13,07) — активация SSI (после ClientReady как в QIP)
                await SendSnacAsync(0x13, 0x07, 0x0000, GetNextRequestID(), null);

                Debug.WriteLine("[Init] Инициализация завершена, запускаем receive loop");

                // Запускаем receive loop
                Task.Run(() => ReceiveServerSnacsAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Init ERROR] {ex}");
                throw;
            }
        }

        private async Task InitServicesAsync()
        {
            Debug.WriteLine("[InitServices] Начало по дампу QIP...");
            StatusUpdater?.Invoke("Настраиваем сервисы...");

            // 1. SNAC(01,0E) — запрос своей инфо (без ожидания ответа здесь)
            await SendSnacAsync(0x01, 0x0E, 0x00, GetNextRequestID(), null);

            // 2. SNAC(13,02) — SSI params с телом 000b 0002 000f
            byte[] ssiParamBody = new byte[] { 0x00, 0x0b, 0x00, 0x02, 0x00, 0x0f };
            await SendSnacAsync(0x13, 0x02, 0x00, GetNextRequestID(), ssiParamBody);

            // 3. SNAC(13,04) — запрос контактов (ответ придёт позже)
            await SendSnacAsync(0x13, 0x04, 0x00, GetNextRequestID(), null);

            // 4. SNAC(02,02) — location limits
            await SendSnacAsync(0x02, 0x02, 0x00, GetNextRequestID(), null);

            // 5. SNAC(03,02) — BLM limits с телом 0005 0002 0003
            byte[] blmParamBody = new byte[] { 0x00, 0x05, 0x00, 0x02, 0x00, 0x03 };
            await SendSnacAsync(0x03, 0x02, 0x00, GetNextRequestID(), blmParamBody);

            // 6. SNAC(04,04) — ICBM params
            await SendSnacAsync(0x04, 0x04, 0x00, GetNextRequestID(), null);

            // 7. SNAC(09,02) — privacy limits
            await SendSnacAsync(0x09, 0x02, 0x00, GetNextRequestID(), null);

            Debug.WriteLine("[InitServices] Все запросы отправлены, ждём ответы...");

            // Теперь ждём ответы — пропускаем всё лишнее пока не получим 13,06
            var contacts = new ObservableCollection<Contact>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

            while (DateTime.UtcNow < deadline)
            {
                var flap = await ReceiveFlapWithTimeout(TimeSpan.FromSeconds(5));
                if (flap == null || flap.Channel != 0x02 || flap.Data.Length < 10) continue;

                var snac = SnacPacket.Parse(flap.Data);
                if (snac == null) continue;

                Debug.WriteLine($"[InitServices] Получен SNAC({snac.Family:X2},{snac.Subtype:X2})");

                if (snac.Family == 0x13 && snac.Subtype == 0x06)
                {
                    ParseContactListPacket(snac.Data, contacts);
                    this.contacts = contacts;
                    Debug.WriteLine($"[InitServices] Получили контакты: {contacts.Count}");

                    if (!SnacFlags.HasMoreData(snac.Flags))
                        break;
                }
                // остальные пакеты (01,0F / 13,03 / 02,03 / 03,03 / 04,05 / 09,03) просто пропускаем
            }

            await ContactStorage.SaveContactsToFileAsync(_uin, contacts);
            Debug.WriteLine("[InitServices] Готово.");
        }

        private async Task SendRateLimitsAckAsync(byte[] data)
        {
            // SNAC(01,07) содержит: ushort classCount, затем для каждого класса ushort classId + много данных
            // Нам нужно извлечь classId каждого класса и подтвердить их через SNAC(01,08)
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                try
                {
                    int offset = 0;
                    if (offset + 2 > data.Length) return;
                    ushort classCount = ReadU16(data, ref offset);
                    Debug.WriteLine($"[RateLimitsAck] classCount={classCount}");

                    for (int i = 0; i < classCount; i++)
                    {
                        if (offset + 2 > data.Length) break;
                        ushort classId = ReadU16(data, ref offset);
                        writer.Write(SwapUInt16(classId));

                        // Каждый класс содержит ещё 33 байта данных (window size, clear/alert/limit/disconnect/current level + flags)
                        offset += 33;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RateLimitsAck ERROR] {ex.Message}");
                    return;
                }

                byte[] payload = ms.ToArray();
                if (payload.Length > 0)
                {
                    await SendSnacAsync(0x01, 0x08, 0x00, GetNextRequestID(), payload);
                    Debug.WriteLine($"[RateLimitsAck] Sent SNAC(01,08) with {payload.Length / 2} class IDs");
                }
            }
        }

        private async Task SendIcbmParametersAsync()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(SwapUInt16(0x0000)); // channel
                writer.Write(SwapUInt32(0x00000003)); // flags: отправка + получение
                writer.Write(SwapUInt16(0x1F40)); // max message snac size
                writer.Write(SwapUInt16(0x03E7)); // max sender warning level
                writer.Write(SwapUInt16(0x03E7)); // max receiver warning level
                writer.Write(SwapUInt32(0x00000000)); // min message interval (ms)

                await SendSnacAsync(0x04, 0x02, 0x00, GetNextRequestID(), ms.ToArray());
                Debug.WriteLine("[ICBM] Sent SNAC(04,02) — ICBM parameters");
            }
        }



        private async Task ClientIdentAsync()
        {
            // здесь сделать SNAC(02,04)
        }

        private byte[] GetMyCapabilities()
        {
            // ICQ capabilities: например, поддержка UTF-8, file transfers и т.д.
            return new byte[]
            {
        0x09, 0x46, 0x13, 0x4C, 0x4C, 0x7F, 0x11, 0xD1,
        0x82, 0x22, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00  // пример capabilities (UTF-8)
            };
        }


       

        private ushort ReadUInt16(byte[] buffer, ref int offset)
        {
            ushort val = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            offset += 2;
            return val;
        }


   /*     private async Task HandleIncomingBuddySnacsAsync()
        {
            while (true)
            {
                var snac = await ReceiveSnacAsync();

                if (snac.Family == 0x03 && snac.Subtype == 0x0B)
                {
                    HandleUserOnline(snac.Data);
                }
                else if (snac.Family == 0x03 && snac.Subtype == 0x0C)
                {
                    HandleUserOffline(snac.Data);
                }
                else if (snac.Family == 0x03 && snac.Subtype == 0x0F)
                {
                    HandleXStatusChanged(snac.Data);
                }
            }
        }

        private void HandleUserOnline(byte[] data)
        {
            int offset = 0;
            string uin = ReadString(data, ref offset);
            uint status = BitConverter.ToUInt32(data, offset); offset += 4;
            uint xstatus = ExtractXStatus(data, offset); // реализуй, если нужно

            AddOrUpdateContact(uin, status, xstatus, isOnline: true);
        }
        */

        private byte[] BuildCapabilitiesPayload()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // TLV 0x01 - MIME type
                writer.Write(SwapUInt16(0x01)); // Type
                writer.Write(SwapUInt16(0x10)); // Length
                writer.Write(Encoding.UTF8.GetBytes("text/x-aolrtf"));
                writer.Write(new byte[16 - "text/x-aolrtf".Length]); // Padding if needed

                // TLV 0x05 - Capabilities (можно задать 1–2 GUID'а)
                writer.Write(SwapUInt16(0x05));
                writer.Write(SwapUInt16(16));
                writer.Write(new byte[16]); // Заглушка (можно заменить на реальные CLSID)

                return ms.ToArray();
            }
        }

        private async Task<FlapFrame> ReceiveSnacAsync(ushort expectedFamily, ushort expectedSubtype)
        {
            while (true)
            {
                var flap = await ReceiveFlapAsync();
                if (flap == null || flap.Data.Length < 10)
                    continue;

                ushort family = (ushort)((flap.Data[0] << 8) | flap.Data[1]);
                ushort subtype = (ushort)((flap.Data[2] << 8) | flap.Data[3]);

                if (family == expectedFamily && subtype == expectedSubtype)
                    return flap;
            }
        }


        private byte[] BuildSsiCheckPayload()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(SwapUInt16(0x0000)); // Last modification time
                writer.Write(SwapUInt16(0x0000)); // Items count
                return ms.ToArray();
            }
        }


        public async Task<SnacPacket> ReceiveSnacWithTimeout(ushort expectedFamily, ushort expectedSubtype, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var flap = await ReceiveFlapWithTimeout(deadline - DateTime.UtcNow);

                    if (flap == null || flap.Channel != 0x02 || flap.Data.Length < 10)
                    {
                        Debug.WriteLine("[ReceiveSnac] Пропущен некорректный или пустой FLAP");
                        continue;
                    }

                    var snac = SnacPacket.Parse(flap.Data);
                    if (snac == null)
                        continue;

                    Debug.WriteLine($"[ReceiveSnac] Получен SNAC 0x{snac.Family:X4}/0x{snac.Subtype:X4}");

                    if (snac.Family == expectedFamily && snac.Subtype == expectedSubtype)
                    {
                        Debug.WriteLine($"[ReceiveSnac] Совпадение SNAC 0x{snac.Family:X4}/0x{snac.Subtype:X4}, длина={snac.Data.Length}");
                        return snac;
                    }
                    else
                    {
                        Debug.WriteLine($"[ReceiveSnac] Ожидался SNAC 0x{expectedFamily:X4}/0x{expectedSubtype:X4}, но пришёл 0x{snac.Family:X4}/0x{snac.Subtype:X4}");
                    }
                }
                catch (TimeoutException)
                {
                    Debug.WriteLine("[ReceiveSnac] Таймаут при ожидании SNAC");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ReceiveSnac] Ошибка: {ex.Message}");
                    break;
                }
            }

            return null;
        }





        public async Task<ObservableCollection<Contact>> GetContactsAsync(uint statusCode)
        {
            // contacts уже заполнен в InitServicesAsync
            if (this.contacts != null && this.contacts.Count > 0)
            {
                Debug.WriteLine($"[GetContacts] Returning cached contacts: {this.contacts.Count}");
                return this.contacts;
            }

            // Если по какой-то причине пусто — возвращаем пустой список
            Debug.WriteLine("[GetContacts] contacts is empty");
            return new ObservableCollection<Contact>();
        }

        public async Task SendClientCapabilitiesAsync()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // SNAC(02,04) header
                ushort family = 0x0002;
                ushort subtype = 0x0004;
                ushort flags = 0x0000;
                uint requestId = GetNextRequestID();

                writer.Write(SwapUInt16(family));       // Family
                writer.Write(SwapUInt16(subtype));      // Subtype
                writer.Write(SwapUInt16(flags));        // Flags
                writer.Write(SwapUInt32(requestId));    // Request ID

                // === TLV(0x01) — MIME type (optional, можно убрать)
                var mime = Encoding.UTF8.GetBytes("kicq4wp 1.0B");
                writer.Write(SwapUInt16(0x0001));                // TLV.Type
                writer.Write(SwapUInt16((ushort)mime.Length));   // TLV.Length
                writer.Write(mime);                              // TLV.Value

                // TLV(0x05): Capabilities list
                var capabilities = new List<byte[]>
        {
            HexToBytes("0946134E4C7F11D18222444553540000"), // UTF8 messages
            HexToBytes("563FC8090B6F41BD9F79422609DFA2F3"), // xStatus (ICQLite)
            HexToBytes("094613494C7F11D18222444553540000"), // Extended messages (channel 2)
            HexToBytes("094613444C7F11D18222444553540000"), // File Transfer
            HexToBytes("094600004C7F11D18222444553540000")  // Voice Chat
        };

                int totalCapsLen = capabilities.Sum(c => c.Length);
                writer.Write(SwapUInt16(0x0005)); // TLV.Type
                writer.Write(SwapUInt16((ushort)totalCapsLen));

                foreach (var cap in capabilities)
                    writer.Write(cap);

                byte[] payload = ms.ToArray();

                Debug.WriteLine("[SendClientCapabilities] Sending SNAC(02,04)");
                Debug.WriteLine("[SendClientCapabilities] Payload: " + BitConverter.ToString(payload));

                await SendFlapAsync(0x02, payload);
            }
        }




        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }


        public void ParseContactListPacket(byte[] data, ObservableCollection<Contact> contacts)
        {
            if (data == null || data.Length < 5)
            {
                Debug.WriteLine("[ParseContactListPacket] Invalid data length");
                return;
            }

            try
            {
                int offset = 0;

                byte version = data[offset++];
                ushort itemCount = ReadU16(data, ref offset);
                Debug.WriteLine($"[ParseContactListPacket] Item count: {itemCount}");

                for (int i = 0; i < itemCount; i++)
                {
                    if (offset + 2 > data.Length) break;
                    ushort nameLen = ReadU16(data, ref offset);

                    if (offset + nameLen > data.Length) break;
                    string name = Encoding.UTF8.GetString(data, offset, nameLen);
                    offset += nameLen;

                    if (offset + 8 > data.Length) break;
                    ushort groupId = ReadU16(data, ref offset);
                    ushort itemId = ReadU16(data, ref offset);
                    ushort itemType = ReadU16(data, ref offset);
                    ushort tlvLen = ReadU16(data, ref offset);

                    if (offset + tlvLen > data.Length)
                    {
                        Debug.WriteLine($"[ParseContactListPacket] Broken TLV block. Len={tlvLen}, Remaining={data.Length - offset}");
                        break;
                    }

                    string displayName = null;

                    int tlvOffset = offset;
                    while (tlvOffset + 4 <= offset + tlvLen)
                    {
                        ushort tlvType = ReadU16(data, ref tlvOffset);
                        ushort tlvValueLen = ReadU16(data, ref tlvOffset);

                        if (tlvOffset + tlvValueLen > offset + tlvLen)
                        {
                            Debug.WriteLine($"[ParseContactListPacket] Invalid TLV. Type=0x{tlvType:X4}, Len={tlvValueLen}, Remaining={offset + tlvLen - tlvOffset}");
                            break;
                        }

                        if (tlvType == 0x0131 && tlvValueLen > 0)
                        {
                            displayName = Encoding.UTF8.GetString(data, tlvOffset, tlvValueLen);
                        }

                        tlvOffset += tlvValueLen;
                    }

                    offset += tlvLen;

                    if (itemType == 0x0000) // Buddy record
                    {
                        string finalName = !string.IsNullOrEmpty(displayName) ? displayName : name;

                        contacts.Add(new Contact
                        {
                            Uin = name,
                            Name = finalName,
                            StatusIcon = "/Assets/statuses/offline.png",
                            IsNewOnline = false
                        });

                        Debug.WriteLine($"[ParseContactListPacket] Added contact: {finalName}");
                    }
                    else
                    {
                        Debug.WriteLine($"[ParseContactListPacket] Skipped item type: 0x{itemType:X4}");
                    }
                }

                // Проверяем время последнего изменения
                if (offset + 4 <= data.Length)
                {
                    uint lastChange = (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                                              (data[offset + 2] << 8) | data[offset + 3]);
                    Debug.WriteLine($"[ParseContactListPacket] Last change time: {lastChange}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ParseContactListPacket ERROR] {ex}");
            }
        }

        private ushort ReadU16(byte[] data, ref int offset)
        {
            ushort val = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            return val;
        }



        public async Task SendSetStatusAsync(uint statusCode)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                uint requestId = GetNextRequestID();

                // TLV(0x06) — статус
                writer.Write(SwapUInt16(0x0006));
                writer.Write(SwapUInt16(0x0004));
                writer.Write(SwapUInt32(statusCode));

                // TLV(0x08) — unknown, присутствует в дампах
                writer.Write(SwapUInt16(0x0008));
                writer.Write(SwapUInt16(0x0002));
                writer.Write(SwapUInt16(0x0000));

                // TLV(0x0C) — DC info
                writer.Write(SwapUInt16(0x000C));
                writer.Write(SwapUInt16(0x0025));
                writer.Write(SwapUInt32(0x00000000)); // internal IP
                writer.Write(SwapUInt32(0x00000000)); // DC tcp port
                writer.Write((byte)0x04);             // DC type
                writer.Write(SwapUInt16(0x0004));     // DC protocol version = 4 (ICQ)
                writer.Write(SwapUInt32(0x00000000)); // DC auth cookie
                writer.Write(SwapUInt32(0x00000000)); // web front port
                writer.Write(SwapUInt32(0x00000003)); // client futures
                uint unixTime = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                writer.Write(SwapUInt32(unixTime));   // last info update time
                writer.Write(SwapUInt32(unixTime));   // last ext info update time
                writer.Write(SwapUInt32(unixTime));   // last ext status update time
                writer.Write(SwapUInt16(0x0000));     // unknown

                await SendSnacAsync(0x01, 0x1E, 0x0000, requestId, ms.ToArray());
                Debug.WriteLine("[SetStatus] Sent SNAC(01,1E) status=0x" + statusCode.ToString("X8"));
            }
        }



        private async Task<bool> ConnectToBosAsync(string bosHostPort, byte[] cookieBytes, uint statusCode)
        {
            try
            {
                Debug.WriteLine($"[BOS] Connecting to BOS server: {bosHostPort}");
                StatusUpdater?.Invoke("Меняем сервер...");
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
                if (hello == null || hello.Channel != 0x01)
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

                Debug.WriteLine($"[BOS] Received response: Type=0x{response.Channel:X2}, Length={response.Data.Length}");

                // Проверяем что это SNAC (0x02) с нужными данными
                if (response.Channel == 0x02 && response.Data.Length >= 10)
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

        public async Task SendIcbmAsync(string toUin, string text)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                uint uptime = (uint)Environment.TickCount;
                uint rand = (uint)new Random().Next();
                writer.Write(SwapUInt32(uptime));
                writer.Write(SwapUInt32(rand));

                writer.Write(SwapUInt16(0x0001)); // channel 1

                byte[] uinBytes = Encoding.UTF8.GetBytes(toUin);
                writer.Write((byte)uinBytes.Length);
                writer.Write(uinBytes);

                // UTF-16 Big Endian
                byte[] msgBytes = Encoding.BigEndianUnicode.GetBytes(text);

                // fragment 0x05: capabilities (text=1)
                byte[] caps = new byte[] { 0x05, 0x01, 0x00, 0x01, 0x01 };

                // fragment 0x01: charset 0x0002 = UTF-16 BE
                byte[] textFrag;
                using (var tfms = new MemoryStream())
                using (var tfw = new BinaryWriter(tfms))
                {
                    tfw.Write((byte)0x01);
                    tfw.Write((byte)0x01);
                    ushort textDataLen = (ushort)(4 + msgBytes.Length);
                    tfw.Write(SwapUInt16(textDataLen));
                    tfw.Write(SwapUInt16(0x0002)); // charset UTF-16 BE
                    tfw.Write(SwapUInt16(0xFFFF)); // language
                    tfw.Write(msgBytes);
                    textFrag = tfms.ToArray();
                }

                byte[] msgData;
                using (var mdms = new MemoryStream())
                {
                    mdms.Write(caps, 0, caps.Length);
                    mdms.Write(textFrag, 0, textFrag.Length);
                    msgData = mdms.ToArray();
                }

                writer.Write(SwapUInt16(0x0002));
                writer.Write(SwapUInt16((ushort)msgData.Length));
                writer.Write(msgData);

                writer.Write(SwapUInt16(0x0006));
                writer.Write(SwapUInt16(0x0000));

                await SendSnacAsync(0x04, 0x06, 0x0000, GetNextRequestID(), ms.ToArray());
                Debug.WriteLine($"[ICBM] Sent to {toUin}: {text}");
            }
        }


        private static string DecodeWin1251(byte[] data, int offset, int length)
{
    // Таблица символов windows-1251 начиная с 0x80
    string high = "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021" +
                  "\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F" +
                  "\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014" +
                  "\uFFFD\u2122\u0459\u203A\u045A\u045C\u045B\u045F" +
                  "\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7" +
                  "\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407" +
                  "\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7" +
                  "\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457" +
                  "\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417" +
                  "\u0418\u0419\u041A\u041B\u041C\u041D\u041E\u041F" +
                  "\u0420\u0421\u0422\u0423\u0424\u0425\u0426\u0427" +
                  "\u0428\u0429\u042A\u042B\u042C\u042D\u042E\u042F" +
                  "\u0430\u0431\u0432\u0433\u0434\u0435\u0436\u0437" +
                  "\u0438\u0439\u043A\u043B\u043C\u043D\u043E\u043F" +
                  "\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447" +
                  "\u0448\u0449\u044A\u044B\u044C\u044D\u044E\u044F";

    var sb = new System.Text.StringBuilder(length);
    for (int i = 0; i < length; i++)
    {
        byte b = data[offset + i];
        if (b < 0x80)
            sb.Append((char)b);
        else
            sb.Append(high[b - 0x80]);
    }
    return sb.ToString();
}

        public async Task<bool> WaitForRedirectOrBosAsync(uint statusCode)
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

                if (flap.Channel == 0x04)
                {
                    StatusUpdater?.Invoke("Получили redirect...");
                    Debug.WriteLine($"[Login] Got redirect FLAP (0x04), Length: {flap.Data.Length}");
                    return await HandleBosRedirectAsync(flap.Data, statusCode);
                }

                Debug.WriteLine($"[Login] Ignoring unexpected FLAP type: 0x{flap.Channel:X2}");
            }
        }

        private async Task ShowMessageDialog(string message)
        {
            Debug.WriteLine($"Showing message dialog: {message}");
            var dialog = new MessageDialog(message);
            await dialog.ShowAsync();
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

        private void HandleIncomingIcbm(byte[] data)
        {
            try
            {
                int offset = 0;

                // skip 8 bytes cookie
                offset += 8;

                if (offset + 2 > data.Length) return;
                ushort channel = ReadU16(data, ref offset);

                if (offset + 1 > data.Length) return;
                byte uinLen = data[offset++];
                if (offset + uinLen > data.Length) return;
                string senderUin = Encoding.UTF8.GetString(data, offset, uinLen);
                offset += uinLen;

                // warning level
                if (offset + 2 > data.Length) return;
                offset += 2;

                // пропускаем фиксированные TLV
                if (offset + 2 > data.Length) return;
                ushort tlvCount = ReadU16(data, ref offset);
                for (int i = 0; i < tlvCount && offset + 4 <= data.Length; i++)
                {
                    int peekOffset = offset + 2;
                    ushort tlvLen = ReadU16(data, ref peekOffset);
                    offset += 4 + tlvLen;
                }

                string text = null;

                if (channel == 0x0001)
                {
                    while (offset + 4 <= data.Length)
                    {
                        ushort tlvType = ReadU16(data, ref offset);
                        ushort tlvLen = ReadU16(data, ref offset);
                        int tlvEnd = offset + tlvLen;
                        if (tlvEnd > data.Length) break;

                        if (tlvType == 0x0002)
                        {
                            int moff = offset;

                            // fragment 0x05 — capabilities: пропускаем
                            if (moff + 4 > tlvEnd) { offset = tlvEnd; break; }
                            if (data[moff] == 0x05)
                            {
                                moff += 2; // id + version
                                ushort capLen = (ushort)((data[moff] << 8) | data[moff + 1]);
                                moff += 2 + capLen;
                            }

                            // fragment 0x01 — text
                            if (moff + 4 <= tlvEnd && data[moff] == 0x01)
                            {
                                moff += 2; // id + version
                                ushort textBlockLen = (ushort)((data[moff] << 8) | data[moff + 1]);
                                moff += 2;

                                if (textBlockLen >= 4 && moff + textBlockLen <= tlvEnd)
                                {
                                    // читаем charset
                                    ushort charset = (ushort)((data[moff] << 8) | data[moff + 1]);
                                    moff += 4; // charset + lang

                                    int textLen = textBlockLen - 4;
                                    if (textLen > 0)
                                    {
                                        // charset 0x0002 = UTF-16 BE, иначе Windows-1251
                                        if (charset == 0x0002)
                                            text = Encoding.BigEndianUnicode.GetString(data, moff, textLen);
                                        else
                                            text = charset == 0x0002
    ? Encoding.BigEndianUnicode.GetString(data, moff, textLen)
    : DecodeWin1251(data, moff, textLen);
                                    }
                                }
                            }
                            offset = tlvEnd;
                            break;
                        }
                        else
                        {
                            offset = tlvEnd;
                        }
                    }
                }
                else if (channel == 0x0004)
                {
                    while (offset + 4 <= data.Length)
                    {
                        ushort tlvType = ReadU16(data, ref offset);
                        ushort tlvLen = ReadU16(data, ref offset);
                        int tlvEnd = offset + tlvLen;
                        if (tlvEnd > data.Length) break;

                        if (tlvType == 0x0005 && tlvLen > 8)
                        {
                            int moff = offset;
                            moff += 4; // sender uin LE
                            byte msgType = data[moff++];
                            byte msgFlags = data[moff++];
                            // длина в LE
                            ushort msgLen = (ushort)(data[moff] | (data[moff + 1] << 8));
                            moff += 2;

                            if (msgLen > 0 && moff + msgLen <= tlvEnd)
                            {
                                // channel 4 всегда Windows-1251, null-terminated
                                int len = msgLen;
                                if (len > 0 && data[moff + len - 1] == 0x00) len--;
                                if (len > 0)
                                    text = DecodeWin1251(data, moff, len);
                            }
                            offset = tlvEnd;
                            break;
                        }
                        else
                        {
                            offset = tlvEnd;
                        }
                    }
                }

                if (text != null)
                {
                    // Сохраняем в очередь
                    if (!_pendingMessages.ContainsKey(senderUin))
                        _pendingMessages[senderUin] = new List<string[]>();
                    _pendingMessages[senderUin].Add(new string[]
                    {
        text,
        DateTime.Now.ToString("HH:mm")
                    });

                    IncomingMessage?.Invoke(senderUin, text);

                    string displayName = senderUin;
                    if (contacts != null)
                    {
                        var c = contacts.FirstOrDefault(x => x.Uin == senderUin);
                        if (c != null) displayName = c.Name;
                    }
                    var ignored = NotificationService.Instance.OnMessageReceived(
                        senderUin, displayName, text, _dispatcher);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleIncomingIcbm ERROR] {ex}");
            }
        }

        public List<string[]> GetAndClearPending(string uin)
        {
            if (!_pendingMessages.ContainsKey(uin))
                return new List<string[]>();
            var msgs = _pendingMessages[uin];
            _pendingMessages[uin] = new List<string[]>();
            return msgs;
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
            if (flap.Channel != 0x02 || flap.Data.Length < 10)
                return;

            var snac = SnacPacket.Parse(flap.Data);
            if (snac == null)
                return;

            Debug.WriteLine($"[SNAC] Received: 0x{snac.Family:X4}/0x{snac.Subtype:X4}, " +
                           $"Flags=0x{snac.Flags:X4}, ReqId=0x{snac.RequestId:X8}");

            // Check SNAC flags
            bool moreData = (snac.Flags & 0x0001) != 0; // More data to come
            bool serverBusy = (snac.Flags & 0x0002) != 0; // Server is busy
            bool error = (snac.Flags & 0x8000) != 0; // Error response

            if (error)
            {
                Debug.WriteLine($"[SNAC ERROR] Error in response for 0x{snac.Family:X4}/0x{snac.Subtype:X4}");
                // Handle error (usually error code is first 2 bytes of Data)
                if (snac.Data.Length >= 2)
                {
                    ushort errorCode = (ushort)((snac.Data[0] << 8) | snac.Data[1]);
                    Debug.WriteLine($"[SNAC ERROR] Error code: 0x{errorCode:X4}");
                }
            }

            // Handle specific SNAC types
            switch (snac.Family)
            {
                case 0x0001:
                    switch (snac.Subtype)
                    {
                        case 0x0003:
                            Debug.WriteLine("[SNAC] Service families list");
                            var families = ParseSupportedFamilies(snac.Data);
                            await HandleServiceFamilies(families);
                            break;
                        case 0x0018:
                            Debug.WriteLine("[SNAC] Service versions reply");
                            await HandleServiceVersionsResponse(snac.Data);
                            break;
                    }
                    break;

                case 0x0003:
                    switch (snac.Subtype)
                    {
                        case 0x000B:
                            Debug.WriteLine("[SNAC] User online");
                            await HandleUserOnlineAsync(snac.Data);
                            break;
                        case 0x000C:
                            Debug.WriteLine("[SNAC] User offline");
                            await HandleUserOfflineAsync(snac.Data);
                            break;
                    }
                    break;

                case 0x0004: // ICBM
                    switch (snac.Subtype)
                    {
                        case 0x0007:
                            HandleIncomingIcbm(snac.Data);
                            break;
                    }
                    break;
            }
        }



        private async Task HandleUserOfflineAsync(byte[] data)
        {
            try
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    if (offset + 1 > data.Length) break;
                    byte uinLen = data[offset++];
                    if (uinLen == 0 || offset + uinLen > data.Length) break;

                    string uin = Encoding.UTF8.GetString(data, offset, uinLen);
                    offset += uinLen;

                    // пропускаем warning level + tlv count + TLVs
                    if (offset + 2 > data.Length) break;
                    offset += 2; // warning level

                    if (offset + 2 > data.Length) break;
                    ushort tlvCount = ReadU16(data, ref offset);
                    for (int i = 0; i < tlvCount && offset + 4 <= data.Length; i++)
                    {
                        ushort tlvLen = ReadU16(data, ref new int[] { offset + 2 }[0]);
                        offset += 4 + tlvLen;
                    }

                    Debug.WriteLine($"[HandleUserOffline] {uin} went offline");

                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (contacts == null) return;
                        var contact = contacts.FirstOrDefault(c => c.Uin == uin);
                        if (contact != null)
                        {
                            contact.StatusIcon = "/Assets/statuses/offline.png";
                            contact.IsNewOnline = false;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleUserOffline ERROR] {ex}");
            }
        }

        private async Task HandleServiceFamilies(ushort[] families)
        {
            try
            {
                Debug.WriteLine("[HandleServiceFamilies] Processing server-supported families...");
                Debug.WriteLine($"[HandleServiceFamilies] Server supports: {string.Join(", ", families.Select(f => $"0x{f:X4}"))}");

                // Filter to only families we support
                var supportedFamilies = families.Where(f => IcqSupportedFamilies.Contains(f)).ToArray();

                if (supportedFamilies.Length == 0)
                {
                    Debug.WriteLine("[HandleServiceFamilies] No common families with server!");
                    return;
                }

                Debug.WriteLine($"[HandleServiceFamilies] Requesting versions for: {string.Join(", ", supportedFamilies.Select(f => $"0x{f:X4}"))}");

                // Request service versions for supported families
                await SendServiceVersionsRequestAsync(supportedFamilies);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleServiceFamilies ERROR] {ex.Message}");
            }
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
                if (_reader != null)
                {
                    _reader.DetachStream();
                    _reader.Dispose();
                    _reader = null;
                }

                if (_writer != null)
                {
                    _writer.DetachStream();
                    _writer.Dispose();
                    _writer = null;
                }

                if (_socket != null)
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Disconnect error: " + ex);
            }

        }

        private async Task HandleUserOnlineAsync(byte[] data)
        {
            try
            {
                int offset = 0;

                while (offset < data.Length)
                {
                    // byte: uin string length
                    if (offset + 1 > data.Length) break;
                    byte uinLen = data[offset++];
                    if (uinLen == 0 || offset + uinLen > data.Length) break;

                    // string: uin
                    string uin = Encoding.UTF8.GetString(data, offset, uinLen);
                    offset += uinLen;

                    // word: warning level (unused)
                    if (offset + 2 > data.Length) break;
                    offset += 2;

                    // word: number of TLVs
                    if (offset + 2 > data.Length) break;
                    ushort tlvCount = ReadU16(data, ref offset); // big-endian ReadU16

                    uint status = 0xFFFFFFFF; // неизвестен пока не прочитаем TLV(06)

                    for (int i = 0; i < tlvCount && offset + 4 <= data.Length; i++)
                    {
                        ushort tlvType = ReadU16(data, ref offset);
                        ushort tlvLen = ReadU16(data, ref offset);

                        if (offset + tlvLen > data.Length)
                        {
                            Debug.WriteLine($"[HandleUserOnline] TLV 0x{tlvType:X4} выходит за пределы данных");
                            break;
                        }

                        if (tlvType == 0x0006 && tlvLen == 4)
                        {
                            status = ReadU32(data, ref offset);
                            Debug.WriteLine($"[HandleUserOnline] {uin} status=0x{status:X8}");
                        }
                        else
                        {
                            offset += tlvLen; // пропускаем остальные TLV
                        }
                    }

                    Debug.WriteLine($"[HandleUserOnline] {uin} is online, status=0x{status:X8}");

                    string iconPath = status == 0xFFFFFFFF
                        ? "/Assets/statuses/online.png"
                        : StatusIconHelper.GetIconForStatus(status);

                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (contacts == null) return;
                        var contact = contacts.FirstOrDefault(c => c.Uin == uin);
                        if (contact == null)
                        {
                            Debug.WriteLine($"[HandleUserOnline] Контакт {uin} не найден");
                            return;
                        }
                        contact.StatusIcon = iconPath;
                        contact.IsNewOnline = true;

                        Task.Delay(5000).ContinueWith(_ =>
                            _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                                contact.IsNewOnline = false).AsTask());
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleUserOnline ERROR] {ex}");
            }
        }

        private uint ReadU32(byte[] data, ref int offset)
        {
            uint val = (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                              (data[offset + 2] << 8) | data[offset + 3]);
            offset += 4;
            return val;
        }

        public static class StatusIconHelper
        {
            public static string GetIconForStatus(uint status)
            {
                switch (status & 0xFFFF) // нижние 2 байта — базовый статус
                {
                    case 0x0000: return "/Assets/statuses/online.png";
                    case 0x0001: return "/Assets/statuses/away.png";
                    case 0x0002: return "/Assets/statuses/dnd.png";
                    case 0x0004: return "/Assets/statuses/na.png";
                    case 0x0010: return "/Assets/statuses/inv.png";
                    default: return "/Assets/statuses/unknown.png";
                }
            }
        }


        public static class SnacFlags
        {
            public const ushort MoreData = 0x0001;     // More data fragments coming
            public const ushort ServerBusy = 0x0002;   // Server is busy
            public const ushort Error = 0x8000;        // Error response

            public static bool HasMoreData(ushort flags) => (flags & MoreData) != 0;
            public static bool IsServerBusy(ushort flags) => (flags & ServerBusy) != 0;
            public static bool IsError(ushort flags) => (flags & Error) != 0;
        }


        private byte[] SwapUInt32(uint value)
        {
            return new byte[]
            {
        (byte)((value >> 24) & 0xFF),
        (byte)((value >> 16) & 0xFF),
        (byte)((value >> 8) & 0xFF),
        (byte)(value & 0xFF)
            };
        }



        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _socket?.Dispose();
        }

        public async Task ReceiveServerSnacsAsync()
        {
            Debug.WriteLine("[SnacReceiver] Starting...");
            _receiveCts = new CancellationTokenSource();
            Task.Run(() => KeepAliveLoopAsync(_receiveCts.Token));

            try
            {
                while (!_receiveCts.IsCancellationRequested)
                {
                    var flap = await ReceiveFlapAsync(); // бросит исключение при обрыве
                    if (flap == null) continue;
                    if (flap.Channel == 0x05)
                    {
                        Debug.WriteLine("[SnacReceiver] Got keep alive");
                        continue;
                    }
                    await HandleFlapAsync(flap);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SnacReceiver] Connection lost: " + ex.Message);
                _receiveCts.Cancel();
                throw; //пробрасываем дальше в ReconnectService
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(60000, token);
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        // FLAP channel 0x05, пустые данные
                        await SendFlapAsync(0x05, new byte[0]);
                        Debug.WriteLine("[KeepAlive] Sent");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[KeepAlive] Failed: " + ex.Message);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeepAlive ERROR] " + ex.Message);
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


        public class SnacPacket
        {
            public ushort Family { get; set; }
            public ushort Subtype { get; set; }
            public ushort Flags { get; set; }
            public uint RequestId { get; set; }
            public byte[] Data { get; set; }

            public static SnacPacket Parse(byte[] data)
            {
                if (data == null || data.Length < 10)
                    return null;

                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    return new SnacPacket
                    {
                        Family = (ushort)((reader.ReadByte() << 8) | reader.ReadByte()),
                        Subtype = (ushort)((reader.ReadByte() << 8) | reader.ReadByte()),
                        Flags = (ushort)((reader.ReadByte() << 8) | reader.ReadByte()),
                        RequestId = (uint)((reader.ReadByte() << 24) | (reader.ReadByte() << 16) |
                                           (reader.ReadByte() << 8) | reader.ReadByte()),
                        Data = reader.ReadBytes(data.Length - 10)
                    };
                }
            }

        }




        public class FlapFrame
        {
            public byte StartMarker { get; set; }  // Should always be 0x2A
            public byte Channel { get; set; }      // FLAP channel (0x01-0x05)
            public ushort Sequence { get; set; }   // Sequence number
            public ushort DataLength { get; set; } // Length of data
            public byte[] Data { get; set; }       // Actual payload

            public static FlapFrame Parse(byte[] data)
            {
                if (data == null || data.Length < 6)
                    return null;

                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    return new FlapFrame
                    {
                        StartMarker = reader.ReadByte(),
                        Channel = reader.ReadByte(),
                        Sequence = (ushort)((reader.ReadByte() << 8) | reader.ReadByte()),
                        DataLength = (ushort)((reader.ReadByte() << 8) | reader.ReadByte()),
                        Data = reader.ReadBytes(data.Length - 6)
                    };
                }
            }
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
