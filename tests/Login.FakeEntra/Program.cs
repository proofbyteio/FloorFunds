// Standalone: dotnet run --project tests/Login.FakeEntra   (port 18093, loopback)
await using var s = new Login.FakeEntra.FakeEntraServer(18093);
await s.StartAsync();
Console.WriteLine($"fake Entra ({Login.FakeEntra.FakeEntraServer.Marker}) on {s.BaseUrl}; key {s.KeyFile}");
await Task.Delay(Timeout.Infinite);
