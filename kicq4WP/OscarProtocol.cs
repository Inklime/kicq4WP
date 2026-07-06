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
        public event Action ContactStatusChanged;
        public Action<string> StatusUpdater { get; set; }
        public event Action<string, string> IncomingMessage;
        private Dictionary<ushort, SsiGroup> _ssiGroups = new Dictionary<ushort, SsiGroup>();
        public event Action<List<SearchResult>, bool> SearchResultReceived;
        private ushort _snacRequestId = 1;
        public event Action<string> DisconnectedByServer;

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
        private ushort _icbmMaxSize;

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

        public class SsiGroup
        {
            public ushort GroupId { get; set; }
            public ushort ItemId { get; set; }
            public string Name { get; set; }
            public List<ushort> MemberIds { get; set; }

            public SsiGroup()
            {
                MemberIds = new List<ushort>();
            }
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



        private async Task<FlapFrame> ReceiveFlapAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await _readLock.WaitAsync(cancellationToken);
            try
            {
                // Проверяем что reader ещё жив
                if (_reader == null)
                    throw new OperationCanceledException("Reader disposed");

                uint headerRead = await _reader.LoadAsync(6).AsTask(cancellationToken);
                if (headerRead < 6)
                    throw new Exception("Connection closed by remote host");

                byte[] header = new byte[6];
                _reader.ReadBytes(header);

                var flap = FlapFrame.Parse(header);
                if (flap == null || flap.StartMarker != 0x2A)
                    throw new Exception("Invalid FLAP header");

                if (_reader == null)
                    throw new OperationCanceledException("Reader disposed");

                uint dataRead = await _reader.LoadAsync(flap.DataLength).AsTask(cancellationToken);
                if (dataRead < flap.DataLength)
                    throw new Exception("Connection closed during data read");

                flap.Data = new byte[flap.DataLength];
                _reader.ReadBytes(flap.Data);

                return flap;
            }
            finally
            {
                // Освобождаем только если ещё захвачен
                try { _readLock.Release(); } catch { }
            }
        }




        private async Task<FlapFrame> ReceiveFlapWithTimeout(TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    return await ReceiveFlapAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[Timeout] No response from server");
                    return null;
                }

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

                // Login Stage II (protocol negotiation), финальная часть по спецификации:
                // клиент обязан запросить рейт-лимиты SNAC(01,06), получить SNAC(01,07)
                // и подтвердить их через SNAC(01,08) — только после этого соединение
                // считается "ready". Раньше этот шаг пропускался и сервер это прощал;
                // судя по всему, обновлённый iserverd теперь строго этого требует и
                // рвёт соединение, если ack не пришёл.
                await SendSnacAsync(0x01, 0x06, 0x0000, GetNextRequestID(), null);
                var rateLimitsSnac = await ReceiveSnacWithTimeout(0x0001, 0x0007, TimeSpan.FromSeconds(5));
                if (rateLimitsSnac != null)
                {
                    await SendRateLimitsAckAsync(rateLimitsSnac.Data);
                    Debug.WriteLine("[Init] Rate limits handshake завершён (01,06 -> 01,07 -> 01,08)");
                }
                else
                {
                    Debug.WriteLine("[Init WARNING] Не получили SNAC(01,07) — сервер может позже разорвать соединение");
                }

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

                Debug.WriteLine("[Init] Инициализация завершена");

                // Receive loop НЕ запускаем здесь — его запускает и им владеет
                // вызывающая сторона (ReconnectService.MonitorLoopAsync), чтобы
                // не было двух параллельных читателей одного сокета, что само
                // по себе тоже рвёт соединение с той же ошибкой.
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
                else if (snac.Family == 0x04 && snac.Subtype == 0x05)
                {
                    // Реальные ICBM-параметры сервера — раньше просто отбрасывались,
                    // из-за чего _icbmMaxSize никогда не устанавливался и клиент слал
                    // сообщения "вслепую" по жёстко забитому порогу.
                    ParseIcbmParams(snac.Data);
                }
                // остальные пакеты (01,0F / 13,03 / 02,03 / 03,03 / 09,03) просто пропускаем
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
            // Channel 1 — plain text, flags=0x0B (receive+send+missed notifications)
            using (var ms = new MemoryStream())
            {
                WriteU16BE(ms, 0x0001);       // channel 1
                WriteU32BE(ms, 0x0000000B);   // flags: 0x01 receive + 0x02 send + 0x08 missed msg notify
                WriteU16BE(ms, 0x1F40);       // max message snac size = 8000
                WriteU16BE(ms, 0x03E7);       // max sender warning level
                WriteU16BE(ms, 0x03E7);       // max receiver warning level
                WriteU32BE(ms, 0x00000000);   // min message interval
                await SendSnacAsync(0x04, 0x02, 0x0000, GetNextRequestID(), ms.ToArray());
            }

            await Task.Delay(100);

            // Channel 2 — rich text / rendezvous
            using (var ms = new MemoryStream())
            {
                WriteU16BE(ms, 0x0002);
                WriteU32BE(ms, 0x00000003);   // receive + send
                WriteU16BE(ms, 0x1F40);
                WriteU16BE(ms, 0x03E7);
                WriteU16BE(ms, 0x03E7);
                WriteU32BE(ms, 0x00000000);
                await SendSnacAsync(0x04, 0x02, 0x0000, GetNextRequestID(), ms.ToArray());
            }

            await Task.Delay(100);

            // Channel 4 — old-style ICQ messages
            using (var ms = new MemoryStream())
            {
                WriteU16BE(ms, 0x0004);
                WriteU32BE(ms, 0x00000003);
                WriteU16BE(ms, 0x1F40);
                WriteU16BE(ms, 0x03E7);
                WriteU16BE(ms, 0x03E7);
                WriteU32BE(ms, 0x00000000);
                await SendSnacAsync(0x04, 0x02, 0x0000, GetNextRequestID(), ms.ToArray());
            }

            Debug.WriteLine("[ICBM] Sent SNAC(04,02) for channels 1, 2, 4");
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
            if (data == null || data.Length < 5) return;

            try
            {
                int offset = 0;
                byte version = data[offset++];
                ushort itemCount = ReadU16(data, ref offset);
                Debug.WriteLine("[ParseContactListPacket] Item count: " + itemCount);

                // Временные списки для двухпроходного парсинга
                var tempContacts = new System.Collections.Generic.List<Contact>();
                var tempGroups = new System.Collections.Generic.Dictionary<ushort, SsiGroup>();

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
                    ushort tlvBlockLen = ReadU16(data, ref offset);

                    int tlvEnd = offset + tlvBlockLen;
                    if (tlvEnd > data.Length) break;

                    string displayName = null;
                    var memberIds = new System.Collections.Generic.List<ushort>();

                    int tlvOffset = offset;
                    while (tlvOffset + 4 <= tlvEnd)
                    {
                        ushort tlvType = ReadU16(data, ref tlvOffset);
                        ushort tlvValueLen = ReadU16(data, ref tlvOffset);
                        if (tlvOffset + tlvValueLen > tlvEnd) break;

                        switch (tlvType)
                        {
                            case 0x0131:
                                if (tlvValueLen > 0)
                                    displayName = Encoding.UTF8.GetString(
                                        data, tlvOffset, tlvValueLen);
                                break;
                            case 0x00C8: // member list для групп
                                for (int m = 0; m + 2 <= tlvValueLen; m += 2)
                                {
                                    int moff = tlvOffset + m;
                                    memberIds.Add((ushort)((data[moff] << 8) | data[moff + 1]));
                                }
                                break;
                        }
                        tlvOffset += tlvValueLen;
                    }

                    offset = tlvEnd;

                    switch (itemType)
                    {
                        case 0x0000: // Buddy
                            string finalName = !string.IsNullOrEmpty(displayName)
                                ? displayName : name;
                            tempContacts.Add(new Contact
                            {
                                Uin = name,
                                Name = finalName,
                                GroupId = groupId,
                                ItemId = itemId,
                                StatusIcon = "/Assets/statuses/offline.png",
                                IsNewOnline = false
                            });
                            Debug.WriteLine("[ParseContactListPacket] Buddy: " + finalName +
                                            " uin=" + name + " groupId=" + groupId +
                                            " itemId=" + itemId);
                            break;

                        case 0x0001:
                            {
                                var g = new SsiGroup
                                {
                                    GroupId = groupId,
                                    ItemId = itemId,
                                    Name = name, // "Контакты" придёт как name для groupId=0
                                    MemberIds = memberIds
                                };
                                tempGroups[groupId] = g;
                                _ssiGroups[groupId] = g;
                                Debug.WriteLine("[ParseContactListPacket] Group: " + name +
                                                " groupId=" + groupId + " members=" + memberIds.Count);
                                break;
                            }

                        case 0x0002: Debug.WriteLine("[ParseContactListPacket] Permit: " + name); break;
                        case 0x0003: Debug.WriteLine("[ParseContactListPacket] Deny: " + name); break;
                        case 0x0004: Debug.WriteLine("[ParseContactListPacket] Visibility settings"); break;
                        case 0x000E: Debug.WriteLine("[ParseContactListPacket] Ignore: " + name); break;
                        case 0x000F: Debug.WriteLine("[ParseContactListPacket] Last update date"); break;
                        default:
                            Debug.WriteLine("[ParseContactListPacket] Unknown type 0x" +
                                            itemType.ToString("X4") + " name=" + name);
                            break;
                    }
                }

                // Второй проход — заполняем Group у контактов по groupId
                foreach (var contact in tempContacts)
                {
                    if (tempGroups.ContainsKey(contact.GroupId))
                        contact.Group = tempGroups[contact.GroupId].Name;
                    else
                        contact.Group = ""; // groupId=0 или неизвестная группа

                    contacts.Add(contact);
                }

                // Время последнего изменения
                if (offset + 4 <= data.Length)
                {
                    uint lastChange = ReadU32(data, ref offset);
                    Debug.WriteLine("[ParseContactListPacket] Last change time: " + lastChange);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ParseContactListPacket ERROR] " + ex);
            }
        }

        // Вспомогательный: строим SSI item для отправки
        private byte[] BuildSsiItem(string name, ushort groupId, ushort itemId,
                                      ushort itemType, byte[] tlvData)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "");
                writer.Write(SwapUInt16((ushort)nameBytes.Length));
                writer.Write(nameBytes);
                writer.Write(SwapUInt16(groupId));
                writer.Write(SwapUInt16(itemId));
                writer.Write(SwapUInt16(itemType));

                if (tlvData != null && tlvData.Length > 0)
                {
                    writer.Write(SwapUInt16((ushort)tlvData.Length));
                    writer.Write(tlvData);
                }
                else
                {
                    writer.Write(SwapUInt16(0x0000)); // нет TLV
                }

                return ms.ToArray();
            }
        }

        // Удалить контакт
        public async Task RemoveContactAsync(Contact contact)
        {
            Debug.WriteLine("[SSI] Removing " + contact.Uin);

            // 1. Удаляем запись контакта
            await SendSnacAsync(0x13, 0x0A, 0x00, GetNextRequestID(),
                BuildSsiItem(contact.Uin, contact.GroupId, contact.ItemId, 0x0000, null));

            var r1 = await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));
            if (r1 != null) Debug.WriteLine("[SSI] Delete result received");

            await Task.Delay(150);

            // 2. Обновляем группу — убираем itemId контакта из списка участников
            if (_ssiGroups.ContainsKey(contact.GroupId))
            {
                var group = _ssiGroups[contact.GroupId];
                group.MemberIds.Remove(contact.ItemId);

                // Строим TLV(0x00C9) с новым списком
                byte[] c9Data = new byte[group.MemberIds.Count * 2];
                for (int i = 0; i < group.MemberIds.Count; i++)
                {
                    c9Data[i * 2] = (byte)(group.MemberIds[i] >> 8);
                    c9Data[i * 2 + 1] = (byte)(group.MemberIds[i] & 0xFF);
                }
                byte[] c9Tlv = BuildTlv(0x00C9, c9Data);

                await SendSnacAsync(0x13, 0x11, 0x00, GetNextRequestID(), null); // begin edit
                await Task.Delay(100);

                await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                    BuildSsiItem(group.Name, group.GroupId, group.ItemId, 0x0001, c9Tlv));

                await SendSnacAsync(0x13, 0x12, 0x00, GetNextRequestID(), null); // end edit

                await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));
            }

            // Удаляем из локального кэша
            if (contacts != null)
            {
                var c = contacts.FirstOrDefault(x => x.Uin == contact.Uin);
                if (c != null)
                {
                    await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        contacts.Remove(c);
                    });
                }
            }

            Debug.WriteLine("[SSI] Removed: " + contact.Uin);
        }

        // Переименовать контакт
        public async Task RenameContactAsync(Contact contact, string newName)
        {
            Debug.WriteLine("[SSI] Renaming " + contact.Uin + " -> " + newName);

            byte[] nameTlv = BuildTlv(0x0131, Encoding.UTF8.GetBytes(newName));

            await SendSnacAsync(0x13, 0x11, 0x00, GetNextRequestID(), null); // begin edit
            await Task.Delay(100);

            await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                BuildSsiItem(contact.Uin, contact.GroupId, contact.ItemId, 0x0000, nameTlv));

            await SendSnacAsync(0x13, 0x12, 0x00, GetNextRequestID(), null); // end edit

            var result = await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));
            if (result != null)
            {
                contact.Name = newName;
                if (contacts != null)
                {
                    var c = contacts.FirstOrDefault(x => x.Uin == contact.Uin);
                    if (c != null)
                        await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                            () => c.Name = newName);
                }
                Debug.WriteLine("[SSI] Renamed OK");
            }
        }

        // Перенести контакт в другую группу
        public async Task MoveContactAsync(Contact contact, ushort newGroupId)
        {
            if (!_ssiGroups.ContainsKey(newGroupId))
            {
                Debug.WriteLine("[SSI] Target group not found: " + newGroupId);
                return;
            }

            var oldGroup = _ssiGroups.ContainsKey(contact.GroupId)
                ? _ssiGroups[contact.GroupId] : null;
            var newGroup = _ssiGroups[newGroupId];

            await SendSnacAsync(0x13, 0x11, 0x00, GetNextRequestID(), null); // begin edit

            // Удаляем из старой группы
            if (oldGroup != null)
            {
                oldGroup.MemberIds.Remove(contact.ItemId);
                byte[] oldC9 = new byte[oldGroup.MemberIds.Count * 2];
                for (int i = 0; i < oldGroup.MemberIds.Count; i++)
                {
                    oldC9[i * 2] = (byte)(oldGroup.MemberIds[i] >> 8);
                    oldC9[i * 2 + 1] = (byte)(oldGroup.MemberIds[i] & 0xFF);
                }
                await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                    BuildSsiItem(oldGroup.Name, oldGroup.GroupId, oldGroup.ItemId,
                                 0x0001, BuildTlv(0x00C9, oldC9)));
            }

            // Обновляем запись контакта с новым groupId
            await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                BuildSsiItem(contact.Uin, newGroupId, contact.ItemId, 0x0000, null));

            // Добавляем в новую группу
            newGroup.MemberIds.Add(contact.ItemId);
            byte[] newC9 = new byte[newGroup.MemberIds.Count * 2];
            for (int i = 0; i < newGroup.MemberIds.Count; i++)
            {
                newC9[i * 2] = (byte)(newGroup.MemberIds[i] >> 8);
                newC9[i * 2 + 1] = (byte)(newGroup.MemberIds[i] & 0xFF);
            }
            await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                BuildSsiItem(newGroup.Name, newGroup.GroupId, newGroup.ItemId,
                             0x0001, BuildTlv(0x00C9, newC9)));

            await SendSnacAsync(0x13, 0x12, 0x00, GetNextRequestID(), null); // end edit

            // Ждём ответы
            await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));
            await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));

            // Обновляем локально
            contact.GroupId = newGroupId;
            contact.Group = newGroup.Name;

            Debug.WriteLine("[SSI] Moved " + contact.Uin + " to group " + newGroup.Name);
        }

        // Получить список групп (для UI)
        public List<SsiGroup> GetGroups()
        {
            return new List<SsiGroup>(_ssiGroups.Values);
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

        // Реальный лимит канала 1, который согласовал сервер (заполняется из
        // SNAC(04,05) в ParseIcbmParams). Если сервер ещё не ответил — считаем
        // это неизвестным и подстраховываемся консервативным значением.
        private int GetEffectiveIcbmByteBudget()
        {
            int max = _icbmMaxSize > 0 ? _icbmMaxSize : 2000;

            // Запас на служебные поля SNAC/TLV вокруг текста: cookie(8) + channel(2)
            // + UIN(1+N) + заголовки TLV(0x0002)/фрагментов(0x05,0x01) + TLV(0x0006).
            const int overhead = 96;
            return Math.Max(200, max - overhead);
        }

        // Режем текст на части по границе UTF-16 code unit'ов, не разрывая
        // суррогатные пары (эмодзи и т.п.), чтобы каждая часть укладывалась
        // в реальный лимит сервера.
        private static List<string> SplitTextForIcbm(string text, int maxBytes)
        {
            var chunks = new List<string>();
            int maxChars = Math.Max(1, maxBytes / 2); // BigEndianUnicode = 2 байта на code unit

            int pos = 0;
            while (pos < text.Length)
            {
                int len = Math.Min(maxChars, text.Length - pos);
                if (pos + len < text.Length && char.IsHighSurrogate(text[pos + len - 1]))
                    len--;
                if (len <= 0) len = 1;

                chunks.Add(text.Substring(pos, len));
                pos += len;
            }
            return chunks;
        }

        public async Task SendIcbmAsync(string toUin, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int budget = GetEffectiveIcbmByteBudget();
            int totalBytes = Encoding.BigEndianUnicode.GetByteCount(text);

            if (totalBytes <= budget)
            {
                await SendSingleIcbmAsync(toUin, text);
                return;
            }

            Debug.WriteLine("[ICBM] Сообщение (" + totalBytes + " байт) превышает лимит сервера (" +
                             budget + " байт) — разбиваю на части");

            var chunks = SplitTextForIcbm(text, budget);
            foreach (var chunk in chunks)
            {
                await SendSingleIcbmAsync(toUin, chunk);
                await Task.Delay(150); // небольшая пауза между частями, чтобы не словить rate limit
            }
        }

        private async Task SendSingleIcbmAsync(string toUin, string text)
        {
            Debug.WriteLine("[ICBM] Sending to " + toUin + " (" + text.Length + " chars)");

            byte[] msgBytes = Encoding.BigEndianUnicode.GetBytes(text);
            Debug.WriteLine("[ICBM] Encoded: " + msgBytes.Length + " bytes UTF-16BE");

            using (var ms = new MemoryStream())
            {
                // === 8 байт cookie ===
                uint uptime = (uint)Environment.TickCount;
                uint rand = (uint)new Random().Next();
                WriteU32BE(ms, uptime);
                WriteU32BE(ms, rand);

                // === channel 1 (2 байта BE) ===
                WriteU16BE(ms, 0x0001);

                // === UIN ===
                byte[] uinBytes = Encoding.UTF8.GetBytes(toUin);
                ms.WriteByte((byte)uinBytes.Length);
                ms.Write(uinBytes, 0, uinBytes.Length);

                // === TLV(0x0002) — message data ===
                // Собираем содержимое TLV заранее чтобы знать длину
                using (var tlvMs = new MemoryStream())
                {
                    // Fragment 0x05: capabilities
                    // 05 01 [len_BE_2] [caps...]
                    // caps = { 0x01 } = text capability
                    byte[] caps = new byte[] { 0x01 };
                    tlvMs.WriteByte(0x05); // fragment id
                    tlvMs.WriteByte(0x01); // fragment version
                    WriteU16BEStream(tlvMs, (ushort)caps.Length);
                    tlvMs.Write(caps, 0, caps.Length);

                    // Fragment 0x01: text
                    // 01 01 [len_BE_2] [charset_BE_2] [lang_BE_2] [text...]
                    // len = 2 (charset) + 2 (lang) + msgBytes.Length
                    ushort textFragLen = (ushort)(2 + 2 + msgBytes.Length);
                    tlvMs.WriteByte(0x01); // fragment id
                    tlvMs.WriteByte(0x01); // fragment version
                    WriteU16BEStream(tlvMs, textFragLen);
                    WriteU16BEStream(tlvMs, 0x0002); // charset UTF-16 BE
                    WriteU16BEStream(tlvMs, 0xFFFF); // language
                    tlvMs.Write(msgBytes, 0, msgBytes.Length);

                    byte[] tlvData = tlvMs.ToArray();

                    // Пишем TLV(0x0002)
                    WriteU16BE(ms, 0x0002);
                    WriteU16BE(ms, (ushort)tlvData.Length);
                    ms.Write(tlvData, 0, tlvData.Length);
                }

                // === TLV(0x0006) — store if offline (пустой) ===
                WriteU16BE(ms, 0x0006);
                WriteU16BE(ms, 0x0000);

                byte[] payload = ms.ToArray();
                await SendSnacAsync(0x04, 0x06, 0x0000, GetNextRequestID(), payload);
                Debug.WriteLine("[ICBM] Sent OK, payload=" + payload.Length + " bytes");
            }
        }

        // Вспомогательные методы записи big-endian
        private void WriteU16BE(MemoryStream ms, ushort value)
        {
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private void WriteU32BE(MemoryStream ms, uint value)
        {
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private void WriteU16BEStream(MemoryStream ms, ushort value)
        {
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
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

        private void HandleMetaResponse(byte[] data)
        {
            try
            {
                var results = new List<SearchResult>();
                bool isLast = false;
                ParseSearchResponse(data, results, out isLast);

                if (SearchResultReceived != null)
                    SearchResultReceived(results, isLast);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HandleMetaResponse ERROR] " + ex.Message);
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
                    SoundService.PlayMessage();

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
                        case 0x0005: // ICBM params response
                            ParseIcbmParams(snac.Data);
                            break;
                        case 0x0007:
                            HandleIncomingIcbm(snac.Data);
                            break;
                        case 0x000A: // missed message
                            HandleMissedMessage(snac.Data);
                            break;
                    }
                    break;
                case 0x0015:
                    switch (snac.Subtype)
                    {
                        case 0x0003:
                            Debug.WriteLine("[Search] Got META response, parsing...");
                            HandleMetaResponse(snac.Data);
                            break;
                    }
                    break;
            }
        }

        private void ParseIcbmParams(byte[] data)
        {
            try
            {
                int offset = 0;
                if (data.Length < 16)
                {
                    Debug.WriteLine("[ICBM Params] Too short: " + data.Length +
                                   " hex=" + BitConverter.ToString(data));
                    return;
                }
                ushort channel = ReadU16(data, ref offset);
                uint flags = ReadU32(data, ref offset);
                ushort maxSize = ReadU16(data, ref offset);
                ushort maxSWarn = ReadU16(data, ref offset);
                ushort maxRWarn = ReadU16(data, ref offset);
                uint minIntvl = ReadU32(data, ref offset);

                Debug.WriteLine("[ICBM Params] channel=" + channel +
                                " flags=0x" + flags.ToString("X8") +
                                " maxSize=" + maxSize +
                                " minInterval=" + minIntvl);

                // ВАЖНО: обновляем _icbmMaxSize только для канала 1 (обычный текст).
                // Раньше строка ниже срабатывала для ЛЮБОГО канала — например,
                // отчёт по каналу 2 (rendezvous/файлы) с крошечным maxSize=512
                // затирал нормальный лимит канала 1, из-за чего все сообщения
                // резались до нескольких сотен байт и всё равно не проходили.
                if (channel == 1)
                    _icbmMaxSize = maxSize;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ICBM Params ERROR] " + ex.Message);
            }
        }
        private void HandleMissedMessage(byte[] data)
        {
            try
            {
                int offset = 0;
                if (data.Length < 2) return;
                ushort channel = ReadU16(data, ref offset);

                byte uinLen = data[offset++];
                string uin = Encoding.UTF8.GetString(data, offset, uinLen);
                offset += uinLen;
                offset += 2; // warning

                ushort tlvCount = ReadU16(data, ref offset);
                for (int i = 0; i < tlvCount && offset + 4 <= data.Length; i++)
                {
                    int po = offset + 2;
                    ushort tl = ReadU16(data, ref po);
                    offset += 4 + tl;
                }

                if (offset + 4 > data.Length) return;
                ushort count = ReadU16(data, ref offset);
                ushort reason = ReadU16(data, ref offset);

                string[] reasons = { "Invalid", "Too large", "Rate exceeded",
                              "Sender too evil", "You too evil" };
                string reasonStr = reason < reasons.Length ? reasons[reason] : "Unknown(" + reason + ")";

                Debug.WriteLine("[MissedMsg] from=" + uin + " channel=" + channel +
                                " count=" + count + " reason=" + reasonStr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MissedMsg ERROR] " + ex.Message);
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
                            if (ContactStatusChanged != null) ContactStatusChanged();
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

        // ── Вспомогательный: построить META запрос ──────────────────────────
        private byte[] BuildMetaRequest(ushort subtype, ushort seq, byte[] body)
        {
            uint ownerUin = uint.Parse(_uin);

            using (var inner = new MemoryStream())
            {
                // Всё в little-endian внутри META
                ushort bodyLen = (ushort)(body != null ? body.Length : 0);
                ushort chunkSize = (ushort)(2 + 4 + 2 + 2 + bodyLen); // size field не считает себя

                WriteU16LE(inner, chunkSize);       // data chunk size (LE)
                WriteU32LE(inner, ownerUin);        // owner uin (LE)
                WriteU16LE(inner, 0x07D0);          // META_DATA_REQ (LE)
                WriteU16LE(inner, seq);             // sequence (LE)
                WriteU16LE(inner, subtype);         // subtype (LE)
                if (body != null)
                    inner.Write(body, 0, body.Length);

                byte[] innerData = inner.ToArray();

                // Оборачиваем в TLV(0x0001)
                using (var outer = new MemoryStream())
                {
                    WriteU16BE(outer, 0x0001);              // TLV type
                    WriteU16BE(outer, (ushort)innerData.Length);
                    outer.Write(innerData, 0, innerData.Length);
                    return outer.ToArray();
                }
            }
        }

        private void WriteU16LE(MemoryStream ms, ushort v)
        {
            ms.WriteByte((byte)(v & 0xFF));
            ms.WriteByte((byte)(v >> 8));
        }

        private void WriteU32LE(MemoryStream ms, uint v)
        {
            ms.WriteByte((byte)(v & 0xFF));
            ms.WriteByte((byte)((v >> 8) & 0xFF));
            ms.WriteByte((byte)((v >> 16) & 0xFF));
            ms.WriteByte((byte)((v >> 24) & 0xFF));
        }

        // ── Asciiz строка с LE length-prefix ────────────────────────────────
        private void WriteAsciiz(MemoryStream ms, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            WriteU16LE(ms, (ushort)(b.Length + 1)); // +1 для нулевого байта
            ms.Write(b, 0, b.Length);
            ms.WriteByte(0x00); // null terminator
        }

        // ── Поиск по UIN — subtype 0x051F ───────────────────────────────────
        public async Task<List<SearchResult>> SearchByUinAsync(string uin, ushort seq)
        {
            uint uinNum = uint.Parse(uin);

            using (var body = new MemoryStream())
            {
                // TLV(0x0136) — UIN в виде dword LE
                body.WriteByte(0x36); body.WriteByte(0x01); // type=0x0136 LE
                body.WriteByte(0x04); body.WriteByte(0x00); // len=4 LE
                WriteU32LE(body, uinNum);

                byte[] payload = BuildMetaRequest(0x0569, seq, body.ToArray());
                await SendSnacAsync(0x15, 0x02, 0x0001, GetNextRequestID(), payload);
                Debug.WriteLine("[Search] Sent SearchByUin(TLV) " + uin);
            }

            return await WaitForSearchResults();
        }

        // ── Поиск по деталям — subtype 0x0515 ───────────────────────────────
        public async Task<List<SearchResult>> SearchByDetailsAsync(
            string firstName, string lastName, string nick, ushort seq)
        {
            using (var body = new MemoryStream())
            {
                // Порядок из дампа: nick, first, last — все ASCIIZ с LE len-prefix
                WriteAsciiz(body, nick);
                WriteAsciiz(body, firstName);
                WriteAsciiz(body, lastName);

                byte[] payload = BuildMetaRequest(0x0533, seq, body.ToArray());
                await SendSnacAsync(0x15, 0x02, 0x0001, GetNextRequestID(), payload);
                Debug.WriteLine("[Search] Sent SearchByDetails(whitepages)");
            }

            return await WaitForSearchResults();
        }

        // ── Поиск по email — subtype 0x0529 ─────────────────────────────────
        public async Task<List<SearchResult>> SearchByEmailAsync(string email, ushort seq)
        {
            using (var body = new MemoryStream())
            {
                // TLV(0x015E) — email string
                body.WriteByte(0x5E); body.WriteByte(0x01); // type=0x015E LE
                byte[] emailBytes = Encoding.UTF8.GetBytes(email);
                ushort emailTlvLen = (ushort)(emailBytes.Length + 1); // +null
                body.WriteByte((byte)(emailTlvLen & 0xFF));
                body.WriteByte((byte)(emailTlvLen >> 8));
                body.Write(emailBytes, 0, emailBytes.Length);
                body.WriteByte(0x00); // null terminator

                // TLV(0x0230) — flags (из дампа: 02 01 00 00)
                body.WriteByte(0x30); body.WriteByte(0x02);
                body.WriteByte(0x04); body.WriteByte(0x00);
                body.WriteByte(0x01); body.WriteByte(0x00); body.WriteByte(0x00); body.WriteByte(0x00);

                byte[] payload = BuildMetaRequest(0x0573, seq, body.ToArray());
                await SendSnacAsync(0x15, 0x02, 0x0001, GetNextRequestID(), payload);
                Debug.WriteLine("[Search] Sent SearchByEmail(TLV) " + email);
            }

            return await WaitForSearchResults();
        }

        // ── Приём результатов поиска ─────────────────────────────────────────
        private async Task<List<SearchResult>> ReceiveSearchResults(
            ushort seq, TimeSpan timeout)
        {
            var results = new List<SearchResult>();
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var flap = await ReceiveFlapWithTimeout(deadline - DateTime.UtcNow);
                if (flap == null) break;
                if (flap.Channel != 0x02 || flap.Data.Length < 10) continue;

                var snac = SnacPacket.Parse(flap.Data);
                if (snac == null) continue;

                // Ищем SNAC(15,03)
                if (snac.Family != 0x0015 || snac.Subtype != 0x0003)
                {
                    // Другие пакеты — обрабатываем нормально и продолжаем ждать
                    await HandleFlapAsync(flap);
                    continue;
                }

                // Парсим META ответ
                bool isLast = false;
                ParseSearchResponse(snac.Data, results, out isLast);

                if (isLast) break;
            }

            Debug.WriteLine("[Search] Got " + results.Count + " results");
            return results;
        }

        private Task<List<SearchResult>> WaitForSearchResults()
        {
            var tcs = new TaskCompletionSource<List<SearchResult>>();
            var allResults = new List<SearchResult>();

            Action<List<SearchResult>, bool> handler = null;
            handler = (results, isLast) =>
            {
                allResults.AddRange(results);
                if (isLast)
                {
                    SearchResultReceived -= handler;
                    tcs.TrySetResult(allResults);
                }
            };
            SearchResultReceived += handler;

            // Таймаут 10 секунд
            Task.Delay(10000).ContinueWith(_ =>
            {
                SearchResultReceived -= handler;
                tcs.TrySetResult(allResults);
            });

            return tcs.Task;
        }

        // ── Парсинг SNAC(15,03) ответа ──────────────────────────────────────
        private void ParseSearchResponse(byte[] data, List<SearchResult> results, out bool isLast)
        {
            isLast = false;
            Debug.WriteLine("[ParseSearch] Data length=" + data.Length +
                            " hex=" + BitConverter.ToString(data));
            try
            {
                int offset = 0;
                while (offset + 4 <= data.Length)
                {
                    ushort tlvType = ReadU16(data, ref offset);
                    ushort tlvLen = ReadU16(data, ref offset);
                    Debug.WriteLine("[ParseSearch] TLV type=0x" + tlvType.ToString("X4") +
                                    " len=" + tlvLen);
                    if (offset + tlvLen > data.Length) break;

                    if (tlvType == 0x0001)
                    {
                        Debug.WriteLine("[ParseSearch] Found TLV(0001), parsing META reply...");
                        ParseMetaSearchReply(data, offset, tlvLen, results, out isLast);
                    }
                    offset += tlvLen;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ParseSearch ERROR] " + ex);
            }
        }

        private void ParseMetaSearchReply(byte[] data, int start, int len,
            List<SearchResult> results, out bool isLast)
        {
            isLast = false;
            int offset = start;
            int end = start + len;

            if (offset + 10 > end) return;

            // LE header: chunkSize(2) ownerUin(4) dataType(2) seq(2)
            offset += 2; // chunk size
            offset += 4; // owner uin
            offset += 2; // data type (должен быть 0x07DA)
            offset += 2; // sequence

            if (offset + 2 > end) return;
            ushort subtype = ReadU16LE(data, ref offset);

            Debug.WriteLine("[Search] META reply subtype=0x" + subtype.ToString("X4"));

            // 0x01AE — последний результат (или единственный для UIN поиска)
            // 0x01A4 — промежуточный результат
            if (subtype == 0x01AE)
            {
                isLast = true;
                // Может содержать последний результат
                if (offset + 1 > end) return;
                byte success = data[offset++];
                if (success != 0x0A) return; // не SEARCH_SUCCESS

                var r = ParseSearchRecord(data, ref offset, end);
                if (r != null) results.Add(r);
            }
            else if (subtype == 0x01A4)
            {
                if (offset + 1 > end) return;
                byte success = data[offset++];
                if (success != 0x0A) return;

                var r = ParseSearchRecord(data, ref offset, end);
                if (r != null) results.Add(r);
            }
        }

        private SearchResult ParseSearchRecord(byte[] data, ref int offset, int end)
        {
            try
            {
                if (offset + 2 > end) return null;
                ushort dataSize = ReadU16LE(data, ref offset); // LE!

                if (offset + 4 > end) return null;
                uint uin = ReadU32LE(data, ref offset); // LE!

                string nick = ReadAsciizLE(data, ref offset, end);
                string first = ReadAsciizLE(data, ref offset, end);
                string last = ReadAsciizLE(data, ref offset, end);
                string email = ReadAsciizLE(data, ref offset, end);

                if (offset + 1 > end) return null;
                byte authFlag = data[offset++];

                if (offset + 2 > end) return null;
                ushort onlineStatus = ReadU16LE(data, ref offset);

                if (offset + 1 > end) return null;
                byte gender = data[offset++];

                if (offset + 2 > end) return null;
                ushort age = ReadU16LE(data, ref offset);

                Debug.WriteLine("[Search] Found: uin=" + uin + " nick=" + nick +
                                " name=" + first + " " + last + " online=" + (onlineStatus == 1));

                return new SearchResult
                {
                    Uin = uin.ToString(),
                    Nick = nick,
                    FirstName = first,
                    LastName = last,
                    Email = email,
                    IsOnline = onlineStatus == 1,
                    Gender = gender,
                    Age = age
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ParseSearchRecord ERROR] " + ex.Message);
                return null;
            }
        }

        // ── LE читалки ───────────────────────────────────────────────────────
        private ushort ReadU16LE(byte[] data, ref int offset)
        {
            ushort v = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return v;
        }

        private uint ReadU32LE(byte[] data, ref int offset)
        {
            uint v = (uint)(data[offset] |
                            (data[offset + 1] << 8) |
                            (data[offset + 2] << 16) |
                            (data[offset + 3] << 24));
            offset += 4;
            return v;
        }

        private string ReadAsciizLE(byte[] data, ref int offset, int end)
        {
            if (offset + 2 > end) return "";
            ushort len = ReadU16LE(data, ref offset);
            if (len == 0) return "";
            if (offset + len > end) return "";
            // ASCIIZ: убираем нулевой байт в конце
            int textLen = len > 0 && data[offset + len - 1] == 0 ? len - 1 : len;
            string s = DecodeWin1251(data, offset, textLen);
            offset += len;
            return s;
        }

        // ── Добавление контакта (SNAC 13,08) ────────────────────────────────
        public async Task AddContactAsync(string uin, string displayName)
        {
            // Генерируем уникальный itemId
            ushort newItemId = GenerateItemId();

            // Находим группу General (или первую доступную)
            ushort targetGroupId = 0x0000;
            string targetGroupName = "";
            ushort targetGroupItemId = 0x0000;

            foreach (var g in _ssiGroups.Values)
            {
                // Берём первую не-мастер группу
                if (g.GroupId != 0x0000)
                {
                    targetGroupId = g.GroupId;
                    targetGroupName = g.Name;
                    targetGroupItemId = g.ItemId;
                    break;
                }
            }

            // Если нет групп — создаём General
            if (targetGroupId == 0x0000 && _ssiGroups.Count == 0)
            {
                targetGroupId = 0x0001;
                targetGroupName = "General";
                targetGroupItemId = 0x0000;
            }

            Debug.WriteLine("[AddContact] Adding " + uin + " to group " +
                            targetGroupName + " itemId=" + newItemId);

            // TLV(0x0131) — display name
            byte[] nameTlv = BuildTlv(0x0131, Encoding.UTF8.GetBytes(displayName));

            await SendSnacAsync(0x13, 0x11, 0x00, GetNextRequestID(), null); // begin edit
            await Task.Delay(100);

            // Добавляем контакт
            await SendSnacAsync(0x13, 0x08, 0x00, GetNextRequestID(),
                BuildSsiItem(uin, targetGroupId, newItemId, 0x0000, nameTlv));

            // Обновляем группу — добавляем itemId
            if (_ssiGroups.ContainsKey(targetGroupId))
            {
                var group = _ssiGroups[targetGroupId];
                group.MemberIds.Add(newItemId);

                byte[] c9Data = new byte[group.MemberIds.Count * 2];
                for (int i = 0; i < group.MemberIds.Count; i++)
                {
                    c9Data[i * 2] = (byte)(group.MemberIds[i] >> 8);
                    c9Data[i * 2 + 1] = (byte)(group.MemberIds[i] & 0xFF);
                }
                await SendSnacAsync(0x13, 0x09, 0x00, GetNextRequestID(),
                    BuildSsiItem(targetGroupName, targetGroupId, targetGroupItemId,
                                 0x0001, BuildTlv(0x00C9, c9Data)));
            }

            await SendSnacAsync(0x13, 0x12, 0x00, GetNextRequestID(), null); // end edit

            // Ждём подтверждение
            var r = await ReceiveSnacWithTimeout(0x13, 0x0E, TimeSpan.FromSeconds(5));
            if (r != null)
            {
                // Добавляем в локальный кэш
                var newContact = new Contact
                {
                    Uin = uin,
                    Name = displayName,
                    GroupId = targetGroupId,
                    ItemId = newItemId,
                    Group = targetGroupName,
                    StatusIcon = "/Assets/statuses/offline.png"
                };

                if (contacts != null)
                {
                    await _dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => contacts.Add(newContact));
                }

                if (_ssiGroups.ContainsKey(targetGroupId))
                    _ssiGroups[targetGroupId].MemberIds.Add(newItemId);

                Debug.WriteLine("[AddContact] Added: " + uin);
            }
        }

        private ushort GenerateItemId()
        {
            // Находим максимальный существующий itemId и берём следующий
            ushort maxId = 0;
            if (contacts != null)
            {
                foreach (var c in contacts)
                    if (c.ItemId > maxId) maxId = c.ItemId;
            }
            return (ushort)(maxId + 1);
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
                Debug.WriteLine("[OscarProtocol] Disconnecting...");

                // Отменяем receive loop и keep alive первым делом
                if (_receiveCts != null)
                {
                    _receiveCts.Cancel();
                    _receiveCts = null;
                }

                // Ждём немного чтобы receive loop успел выйти
                await Task.Delay(300);

                // Освобождаем readLock если он захвачен
                if (_readLock.CurrentCount == 0)
                {
                    try { _readLock.Release(); } catch { }
                }

                // Отправляем disconnect FLAP
                try
                {
                    if (_writer != null)
                    {
                        await SendFlapAsync(0x04, new byte[0]);
                        await Task.Delay(100);
                    }
                }
                catch { }

                // Закрываем потоки
                try { _writer?.DetachStream(); } catch { }
                try { _writer?.Dispose(); } catch { }
                _writer = null;

                try { _reader?.DetachStream(); } catch { }
                try { _reader?.Dispose(); } catch { }
                _reader = null;

                try { _socket?.Dispose(); } catch { }
                _socket = null;

                Debug.WriteLine("[OscarProtocol] Disconnected.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[OscarProtocol] Disconnect error: " + ex.Message);
            }
        }

        private void HandleServerInitiatedDisconnect(byte[] data)
        {
            string reason = "Соединение закрыто сервером";
            ushort code = 0;

            try
            {
                var tlvs = ParseTlvs(data);

                TLV codeTlv;
                if (tlvs.TryGetValue(0x0009, out codeTlv) && codeTlv.Value.Length >= 2)
                    code = (ushort)((codeTlv.Value[0] << 8) | codeTlv.Value[1]);

                TLV textTlv;
                if (tlvs.TryGetValue(0x000B, out textTlv))
                    reason = Encoding.UTF8.GetString(textTlv.Value, 0, textTlv.Value.Length);
                SoundService.PlayError();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Disconnect] Не удалось разобрать причину: " + ex.Message);
            }

            Debug.WriteLine("[Disconnect] code=0x" + code.ToString("X4") + " reason=" + reason);
            DisconnectedByServer?.Invoke(reason);
        }

        private async Task HandleUserOnlineAsync(byte[] data)
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

                    if (offset + 2 > data.Length) break;
                    offset += 2; // warning level

                    if (offset + 2 > data.Length) break;
                    ushort tlvCount = ReadU16(data, ref offset);

                    // Собираем всю информацию из TLV
                    var info = new ContactInfo { Uin = uin };
                    uint status = 0;

                    for (int i = 0; i < tlvCount && offset + 4 <= data.Length; i++)
                    {
                        ushort tlvType = ReadU16(data, ref offset);
                        ushort tlvLen = ReadU16(data, ref offset);
                        int tlvEnd = offset + tlvLen;

                        if (tlvEnd > data.Length) break;

                        switch (tlvType)
                        {
                            case 0x0001: // user class
                                if (tlvLen >= 2)
                                    info.UserClass = ReadU16(data, ref offset);
                                break;

                            case 0x0006: // user status
                                if (tlvLen >= 4)
                                {
                                    status = ReadU32(data, ref offset);
                                    info.Status = status;
                                }
                                break;

                            case 0x000A: // external IP
                                if (tlvLen >= 4)
                                    info.ExternalIp = ReadU32(data, ref offset);
                                break;

                            case 0x000F: // online time (seconds)
                                if (tlvLen >= 4)
                                    info.OnlineTime = ReadU32(data, ref offset);
                                break;

                            case 0x0003: // signon time
                                if (tlvLen >= 4)
                                    info.SignonTime = ReadU32(data, ref offset);
                                break;

                            case 0x0005: // member since
                                if (tlvLen >= 4)
                                    info.MemberSince = ReadU32(data, ref offset);
                                break;

                            case 0x000C: // DC info
                                if (tlvLen >= 9)
                                {
                                    info.DcInternalIp = ReadU32(data, ref offset);
                                    info.DcPort = (ushort)(ReadU32(data, ref offset) & 0xFFFF);
                                    info.DcType = data[offset];
                                }
                                break;
                        }

                        offset = tlvEnd; // всегда прыгаем в конец TLV
                    }

                    Debug.WriteLine("[UserOnline] " + uin + " status=0x" + status.ToString("X8"));

                    await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (contacts == null) return;
                        var contact = contacts.FirstOrDefault(c => c.Uin == uin);
                        if (contact == null) return;

                        contact.StatusIcon = StatusIconHelper.GetIconForStatus(status);
                        contact.Info = info;
                        contact.IsNewOnline = true;

                        Task.Delay(5000).ContinueWith(_ =>
                            _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                                contact.IsNewOnline = false).AsTask());
                        if (contact.StatusIcon.Contains("offline"))
                            SoundService.PlayOnline();
                    });

                    if (ContactStatusChanged != null)
                        ContactStatusChanged();

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HandleUserOnline ERROR] " + ex);
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
                // Базовый статус — нижние 2 байта
                ushort baseStatus = (ushort)(status & 0xFFFF);

                switch (baseStatus)
                {
                    case 0x0000: return "/Assets/statuses/online.png";
                    case 0x0001: return "/Assets/statuses/away.png";
                    case 0x0002: return "/Assets/statuses/dnd.png";
                    case 0x0004: return "/Assets/statuses/na.png";
                    case 0x0010: return "/Assets/statuses/busy.png";   // occupied
                    case 0x0020: return "/Assets/statuses/f4c.png";    // free4chat
                    case 0x0100: return "/Assets/statuses/inv.png";    // invisible

                    case 0x3000: return "/Assets/statuses/angry.png";  // злой
                    case 0x4000: return "/Assets/statuses/depressed.png"; // депрессия
                    case 0x5000: return "/Assets/statuses/home.png";   // дома
                    case 0x6000: return "/Assets/statuses/work.png";   // работа
                    case 0x2001: return "/Assets/statuses/eating.png"; // обед (0x1001 флаг + 0x2000?)

                    default:
                        Debug.WriteLine("[Status] Unknown status=0x" + status.ToString("X8") +
                                        " base=0x" + baseStatus.ToString("X4"));
                        return "/Assets/statuses/online.png";
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
                    var flap = await ReceiveFlapAsync();
                    if (flap == null) continue;
                    if (flap.Channel == 0x05) continue;

                    if (flap.Channel == 0x04)
                    {
                        HandleServerInitiatedDisconnect(flap.Data);
                        return; // сервер сам закроет сокет следующим пакетом — читать больше нечего
                    }

                    await HandleFlapAsync(flap);
                }
            }
            catch (OperationCanceledException)
            {
                // Намеренное отключение — не ошибка
                Debug.WriteLine("[SnacReceiver] Cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SnacReceiver] Connection lost: " + ex.Message);
                _receiveCts?.Cancel();
                throw; // пробрасываем только реальные ошибки для ReconnectService
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