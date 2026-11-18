using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FactoryMES.Contracts;
using FactoryMES.EquipmentConnector.Protocols;

namespace FactoryMES.Tooling;

// 실제 장비·Oracle 원장을 바꾸지 않고 바이트 해석과 독립 TCP 응답을 검증한다.
public static class ModbusProtocolVerification
{
    public static async Task RunAsync()
    {
        var checks = new List<string>();
        void Check(bool valid, string name)
        {
            if (!valid)
            {
                throw new InvalidOperationException(name);
            }
            lock (checks)
            {
                checks.Add(name);
            }
            Console.WriteLine("PASS " + name);
        }

        decimal Decode(
            string type,
            string order,
            params ushort[] registers
        )
        {
            Check(ModbusFormat.TryTag("holding:7:" + type + ":" + order, out var tag),
                "Parse " + type + " " + order);
            return ModbusEquipmentReader.DecodeRegisters(
                registers,
                7,
                tag
            );
        }

        Check(Decode(
            "UINT16",
            "AB",
            0x1234
        ) == 0x1234, "UINT16 AB big endian");
        Check(Decode(
            "UINT16",
            "BA",
            0x3412
        ) == 0x1234, "UINT16 BA byte swap");
        Check(Decode(
            "INT16",
            "AB",
            0xFF85
        ) == -123, "INT16 negative sign preserved");
        Check(Decode(
            "UINT32",
            "ABCD",
            0x1234,
            0x5678
        ) == 0x12345678,
            "UINT32 ABCD word order");
        Check(Decode(
            "UINT32",
            "CDAB",
            0x5678,
            0x1234
        ) == 0x12345678,
            "UINT32 CDAB word order");
        Check(Decode(
            "UINT32",
            "BADC",
            0x3412,
            0x7856
        ) == 0x12345678,
            "UINT32 BADC word order");
        Check(Decode(
            "UINT32",
            "DCBA",
            0x7856,
            0x3412
        ) == 0x12345678,
            "UINT32 DCBA word order");
        Check(Decode(
            "UINT32",
            "ABCD",
            0xFFFF,
            0xFFFF
        ) == uint.MaxValue,
            "UINT32 does not overflow signed integer");
        Check(Decode(
            "INT32",
            "ABCD",
            0xFFFF,
            0xFF85
        ) == -123,
            "INT32 negative sign preserved");
        Check(Decode(
            "FLOAT32",
            "ABCD",
            0x42F7,
            0x0000
        ) == 123.5m,
            "FLOAT32 finite value decoded");
        Check(Decode(
            "FLOAT32",
            "CDAB",
            0x0000,
            0x42F7
        ) == 123.5m,
            "FLOAT32 swapped words decoded");
        foreach (var words in new[]
        {
            new ushort[] {
                0x7FC0,
                0x0000
            },
            new ushort[] {
                0x7F80,
                0x0000
            },
            new ushort[] {
                0xFF80,
                0x0000
            }
        })
        {
            var refused = false;
            try
            {
                Decode(
                    "FLOAT32",
                    "ABCD",
                    words
                );
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            Check(refused, "Non-finite FLOAT32 refused " + words[0]);
        }
        var boundsRefused = false;
        try
        {
            ModbusEquipmentReader.DecodeRegisters(
                [
                    1
                ],
                7,
                new ModbusTag(
                    7,
                    2,
                    "UINT32",
                    "ABCD"
                )
            );
        }
        catch (InvalidOperationException)
        {
            boundsRefused = true;
        }
        Check(boundsRefused, "Incomplete multiword value refused");
        foreach (var path in new[]
        {
            "holding:65535:UINT32:ABCD",
            "holding:-1:UINT16:AB",
            "holding:01:UINT16:AB",
            "holding:0:UINT16:ABCD",
            "holding:0:FLOAT64:ABCD",
            "input:0:UINT16:AB"
        })
        {
            Check(!ModbusFormat.TryTag(path, out _), "Invalid tag refused " + path);
        }
        Check(ModbusFormat.TryTag("holding:65534:UINT32:ABCD", out _),
            "Last complete UINT32 address accepted");
        foreach (var endpoint in new[]
        {
            "modbus.tcp://127.0.0.1:1502/unit/1",
            "modbus.tcp://modbus-simulator:502/unit/1",
            "modbus.tcp://modbus-simulator:1502/unit/0",
            "modbus.tcp://modbus-simulator:1502/unit/248",
            "modbus.tcp://modbus-simulator:1502/unit/01",
            "modbus.tcp://user@modbus-simulator:1502/unit/1",
            "modbus.tcp://modbus-simulator:1502/unit/1?x=1",
            "modbus.tcp://modbus-simulator:1502/unit/%31"
        })
        {
            Check(!ModbusFormat.TryEndpoint(
                endpoint,
                out _,
                out _
            ),
                "Unapproved Modbus endpoint refused");
        }
        Check(ModbusFormat.TryEndpoint(
            "modbus.tcp://modbus-simulator:1502/unit/247",
            out _,
            out var lastUnit
        ) && lastUnit == 247, "Approved endpoint and unit parsed");
        MappingInput Mapping(
            string path,
            string type = "INTEGER",
            string kind = "NUMBER"
        ) => new(
            "1",
            "CHECK",
            path,
            type,
            kind,
            "EA",
            "1",
            "0",
            true
        );
        Check(ModbusFormat.ValidMapping(Mapping("holding:0:UINT32:ABCD", kind: "COUNTER")),
            "Unsigned COUNTER mapping accepted");
        Check(!ModbusFormat.ValidMapping(Mapping("holding:0:UINT32:ABCD", kind: "COUNTER")
            with {
                Scale = "0.1"
            }), "COUNTER fractional scale rejected at configuration boundary");
        Check(!ModbusFormat.ValidMapping(Mapping("holding:0:UINT32:ABCD", kind: "COUNTER")
            with {
                Offset = "1"
            }), "COUNTER offset rejected at configuration boundary");
        Check(!ModbusFormat.ValidMapping(Mapping("holding:0:INT32:ABCD", kind: "COUNTER")),
            "Signed COUNTER mapping refused");
        Check(!ModbusFormat.ValidMapping(Mapping(
            "holding:0:FLOAT32:ABCD",
            "DECIMAL",
            "COUNTER"
        )),
            "Floating COUNTER mapping refused");
        Check(!ModbusFormat.ValidMapping(Mapping("holding:0:FLOAT32:ABCD")),
            "Wire type and DataType mismatch refused");
        Check(ModbusFormat.ValidSpan(
            [
                Mapping("holding:0:UINT16:AB"),
                Mapping("holding:124:UINT16:AB")
            ]
        ), "FC03 125-register block accepted");
        Check(!ModbusFormat.ValidSpan(
            [
                Mapping("holding:0:UINT16:AB"),
                Mapping("holding:125:UINT16:AB")
            ]
        ), "FC03 126-register block refused");

        byte[] Response(byte[] request, byte? unit = null)
        {
            var frame = new byte[13];
            request.AsSpan(0, 4).CopyTo(frame);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 7);
            frame[6] = unit ?? request[6];
            frame[7] = 3;
            frame[8] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9), 0x1234);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11), 0x5678);
            return frame;
        }

        async Task Wire(
            string name,
            Func<byte[], byte[]> response,
            bool expectedSuccess,
            bool fragmented = false,
            bool stalled = false,
            bool callerCancellation = false
        )
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(shutdown.Token);
                using var stream = peer.GetStream();
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, shutdown.Token);
                Check(request[7] == 3 && request[6] == 17
                    && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)) == 7
                    && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)) == 2,
                    name + " uses FC03, exact unit and zero-based address/count");
                if (stalled)
                {
                    await Task.Delay(3000, shutdown.Token);
                    return;
                }
                var frame = response(request);
                if (fragmented)
                {
                    foreach (var value in frame)
                    {
                        await stream.WriteAsync(new byte[] {
                            value
                        }, shutdown.Token);
                        await Task.Delay(2, shutdown.Token);
                    }
                }
                else
                {
                    await stream.WriteAsync(frame, shutdown.Token);
                }
            }, shutdown.Token);
            using var client = new TcpClient();
            using var deadline = new CancellationTokenSource(
                callerCancellation ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(700)
            );
            var elapsed = Stopwatch.StartNew();
            Exception? failure = null;
            ushort[]? value = null;
            try
            {
                await client.ConnectAsync(
                    IPAddress.Loopback,
                    port,
                    deadline.Token
                );
                value = await ModbusEquipmentReader.ReadRegistersAsync(
                    client,
                    17,
                    7,
                    2,
                    300,
                    deadline.Token
                );
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                shutdown.Cancel();
                listener.Stop();
                try
                {
                    await server;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
            Check(expectedSuccess
                ? failure is null && value is [0x1234, 0x5678]
                : failure is not null, name);
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(3), name + " is bounded");
            if (callerCancellation)
            {
                Check(failure is OperationCanceledException,
                    "Caller cancellation closes pending socket I/O");
            }
        }

        await Wire(
            "Fragmented TCP response reassembled",
            x => Response(x),
            true,
            fragmented: true
        );
        await Wire(
            "Modbus exception response refused",
            request =>
            {
                var frame = new byte[9];
                request.AsSpan(0, 4).CopyTo(frame);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 3);
                frame[6] = request[6];
                frame[7] = 0x83;
                frame[8] = 2;
                return frame;
            },
            false
        );
        await Wire(
            "Transaction mismatch refused",
            request =>
            {
                var frame = Response(request);
                frame[1] ^= 1;
                return frame;
            },
            false
        );
        await Wire(
            "Unit mismatch refused",
            request => Response(request, 18),
            false
        );
        await Wire(
            "Non-Modbus protocol identifier refused",
            request =>
            {
                var frame = Response(request);
                frame[3] = 1;
                return frame;
            },
            false
        );
        await Wire(
            "Extra byte inside declared MBAP payload refused",
            request =>
            {
                var frame = new byte[14];
                Response(request).CopyTo(frame, 0);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 8);
                frame[13] = 0x7F;
                return frame;
            },
            false
        );
        await Wire(
            "FC03 byte count mismatch refused",
            request =>
            {
                var frame = Response(request);
                frame[8] = 2;
                return frame;
            },
            false
        );
        await Wire(
            "Unexpected response function refused",
            request =>
            {
                var frame = Response(request);
                frame[7] = 4;
                return frame;
            },
            false
        );
        await Wire(
            "MBAP payload below minimum refused",
            request =>
            {
                var frame = Response(request);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 1);
                return frame;
            },
            false
        );
        await Wire(
            "MBAP payload above FC03 maximum refused",
            request =>
            {
                var frame = Response(request);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 254);
                return frame;
            },
            false
        );
        await Wire(
            "Truncated response refused",
            request => Response(request)[..10],
            false
        );
        await Wire(
            "Stalled read timeout",
            request => Response(request),
            false,
            stalled: true
        );
        await Wire(
            "Explicit cancellation is bounded",
            request => Response(request),
            false,
            stalled: true,
            callerCancellation: true
        );

        var directory = Environment.GetEnvironmentVariable("MES_REPORT_DIRECTORY") ?? "/reports";
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "modbus-protocol-verification.json"),
            JsonSerializer.Serialize(new
            {
                passed = checks.Count,
                checks,
                realTcpFixture = true,
                hardwareCalls = 0,
                cloudApiCalls = 0,
                externalDeliveryCalls = 0,
                note = "Independent raw TCP peer and pure decoding checks; separate from simulator E2E."
            }, new JsonSerializerOptions {
                WriteIndented = true
            })
        );
    }
}



