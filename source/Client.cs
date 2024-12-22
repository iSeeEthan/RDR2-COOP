using System;
using System.Threading.Tasks;
using RDR2;
using RDR2.Math;
using RDR2.UI;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using System.Windows.Forms;
using RDR2.Native;

namespace RDR2CoopMod
{
    public class PlayerData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Heading { get; set; }
        public int Health { get; set; }
        public bool IsJumping { get; set; }
        public bool IsSprinting { get; set; }
        public bool IsWalking { get; set; }
    }

    public class CoopMod : Script
    {
        private readonly Uri _serverUri = new Uri("ws://localhost:8765");
        private ClientWebSocket _websocketClient;
        private CancellationTokenSource _cancellationTokenSource;
        private Ped _remotePlayer;
        private bool _isConnected;
        private Task _receiveTask;
        private readonly object _lockObject = new object();
        private Model _playerModel;
        private bool _shouldUpdateRemotePlayer;
        private PlayerData _lastReceivedData;
        private DateTime _lastCleanupTime = DateTime.Now;
        private const int CLEANUP_INTERVAL_MS = 5000; // Cleanup every 5 seconds

        public CoopMod()
        {
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }

        private void OnAborted(object sender, EventArgs e)
        {
            CleanupResources();
        }

        private async void SetupWebsocket()
        {
            CleanupResources(); // Cleanup any existing resources

            _cancellationTokenSource = new CancellationTokenSource();
            _websocketClient = new ClientWebSocket();

            try
            {
                await _websocketClient.ConnectAsync(_serverUri, _cancellationTokenSource.Token);
                _receiveTask = ReceiveLoop();
            }
            catch (Exception)
            {
                CleanupResources();
                _isConnected = false;
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[4096];

            while (_websocketClient?.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _websocketClient.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var data = JsonConvert.DeserializeObject<PlayerData>(message);

                        lock (_lockObject)
                        {
                            _lastReceivedData = data;
                            _shouldUpdateRemotePlayer = true;
                        }
                    }
                }
                catch (Exception)
                {
                    break; // Exit the loop on any error
                }
            }
        }

        private void UpdateRemotePlayer()
        {
            if (_lastReceivedData == null) return;

            try
            {
                if (_remotePlayer == null || !_remotePlayer.Exists())
                {
                    _playerModel.MarkAsNoLongerNeeded();
                    _playerModel = new Model(PedHash.CS_Cassidy);
                    _playerModel.Request(1000);

                    if (_playerModel.IsLoaded)
                    {
                        _remotePlayer = World.CreatePed(_playerModel, Game.Player.Character.Position);
                    }
                    return; // Wait for next tick if we just created the player
                }

                var newPosition = new Vector3(
                    _lastReceivedData.X,
                    _lastReceivedData.Y,
                    _lastReceivedData.Z
                );

                _remotePlayer.Position = newPosition;
                _remotePlayer.Heading = _lastReceivedData.Heading;
                _remotePlayer.Health = _lastReceivedData.Health;

                // Only update tasks if significant movement is needed
                if (Vector3.Distance(_remotePlayer.Position, newPosition) > 0.1f)
                {
                    if (_lastReceivedData.IsJumping)
                        _remotePlayer.Task.Jump();
                    else if (_lastReceivedData.IsSprinting)
                        _remotePlayer.Task.RunTo(newPosition);
                    else if (_lastReceivedData.IsWalking)
                        _remotePlayer.Task.GoTo(newPosition);
                }
            }
            catch (Exception) { } // Silently handle any native call errors
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (!_isConnected) return;

            // Periodic cleanup
            if ((DateTime.Now - _lastCleanupTime).TotalMilliseconds > CLEANUP_INTERVAL_MS)
            {
                PerformPeriodicCleanup();
                _lastCleanupTime = DateTime.Now;
            }

            // Update remote player if needed
            if (_shouldUpdateRemotePlayer)
            {
                lock (_lockObject)
                {
                    UpdateRemotePlayer();
                    _shouldUpdateRemotePlayer = false;
                }
            }

            // Send local player data
            if (_websocketClient?.State == WebSocketState.Open)
            {
                try
                {
                    var localPlayer = Game.Player.Character;
                    var playerData = new PlayerData
                    {
                        X = localPlayer.Position.X,
                        Y = localPlayer.Position.Y,
                        Z = localPlayer.Position.Z,
                        Heading = localPlayer.Heading,
                        Health = localPlayer.Health,
                        IsJumping = localPlayer.IsJumping,
                        IsSprinting = localPlayer.IsSprinting,
                        IsWalking = localPlayer.IsWalking
                    };

                    var json = JsonConvert.SerializeObject(playerData);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _websocketClient.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        _cancellationTokenSource.Token);
                }
                catch (Exception) { } // Silently handle send errors
            }
        }

        private void PerformPeriodicCleanup()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void CleanupResources()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _websocketClient?.Abort();
                _websocketClient?.Dispose();
                _cancellationTokenSource?.Dispose();

                if (_remotePlayer != null && _remotePlayer.Exists())
                {
                    _remotePlayer.Delete();
                    _remotePlayer = null;
                }

                _playerModel.MarkAsNoLongerNeeded();
                _playerModel = null;

                _lastReceivedData = null;
                _shouldUpdateRemotePlayer = false;

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception) { }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F5) return;

            _isConnected = !_isConnected;

            if (_isConnected)
            {
                SetupWebsocket();
            }
            else
            {
                CleanupResources();
            }
        }
    }
}