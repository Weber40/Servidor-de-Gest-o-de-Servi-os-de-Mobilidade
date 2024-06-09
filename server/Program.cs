using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Security.Authentication.ExtendedProtection;
using System.Text;
using System.Threading;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;


class Servidor
{
    private static Mutex mut = new Mutex();
    private static Dictionary<string, string> adminCredentials = new Dictionary<string, string>()
    {
        { "admin", "password123" } // Exemplos de credenciais p/ administrador
    };


    public static void Main()
    {
        string sourceDirectory = Directory.GetParent(System.IO.Directory.GetCurrentDirectory()).Parent.Parent.FullName;
        string destinationDirectory = Directory.GetCurrentDirectory();

        foreach (var file in Directory.GetFiles(sourceDirectory, "*.csv"))
        {
            string fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDirectory, fileName), true);
        }


        IPAddress ipAddress = IPAddress.Parse("127.0.0.1");
        TcpListener listener = new TcpListener(ipAddress, 13000);

        listener.Start();

        Console.WriteLine("Servidor iniciado...");

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClient));
            clientThread.Start(client);
        }
    }

    private static (string, string) AddNewClient(string clientId)
    {
        var clientServices = File.ReadLines("clientes.csv")
            .Skip(1) // Skip the header line
            .Select(line => line.Split(','))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        var serviceCounts = clientServices.Values.GroupBy(serviceId => serviceId)
            .Select(group => new { ServiceId = group.Key, Count = group.Count() });
        string leastUsedService = serviceCounts.OrderBy(service => service.Count).First().ServiceId;


        clientServices.Add(clientId, leastUsedService);


        var lines = new List<string> { "ClienteID,ServicoID" };
        lines.AddRange(clientServices.Select(kvp => $"{kvp.Key},{kvp.Value}"));
        File.WriteAllLines("clientes.csv", lines);

        return (clientId, leastUsedService);
    }

    private static (string, string) GetTaskInProgress(string serviceId, string clientId)
    {
        string directoryPath = Directory.GetCurrentDirectory();
        string filePath = Path.Combine(directoryPath, "Servico_" + serviceId + ".csv");

        var tasks = File.ReadLines(serviceId + ".csv")
            .Skip(1)
            .Select(line => line.Split(','))
            .Where(parts => parts[2] == "Em curso" && parts[3] == clientId) // Filtrar tarefas em curso do cliente
            .Select(parts => (TaskId: parts[0], Description: parts[1]));

        //devolve a primeira tarefa em curso do cliente
        return tasks.FirstOrDefault();
    }

    private static List<(string, string)> GetAvailableTasks(string serviceId)
    {
        string directoryPath = Directory.GetCurrentDirectory();
        string filePath = Path.Combine(directoryPath, "Servico_" + serviceId + ".csv");

        var tasks = File.ReadLines(filePath)
            .Skip(1) 
            .Select(line => line.Split(','))
            .Where(parts => parts[2] == "Nao alocado") // Filtrar tarefas não alocadas
            .Select(parts => (TaskId: parts[0], Description: parts[1]))
            .ToList();

        return tasks;
    }

    private static (string, string)? AssignTaskToClient(string serviceId, string taskId, string clientId)
    {
        string directoryPath = Directory.GetCurrentDirectory();
        string filePath = Path.Combine(directoryPath, "Servico_" + serviceId + ".csv");

        var lines = File.ReadLines(filePath).ToList();
        var header = lines[0];
        var tasks = lines.Skip(1)
            .Select(line => line.Split(','))
            .ToList();

        var task = tasks.FirstOrDefault(parts => parts[0] == taskId);
        if (task == null || task[2] != "Nao alocado")
        {
            return null; // Tarefa não encontrada ou já alocada
        }

        task[2] = "Em curso";
        task[3] = clientId;

        var updatedLines = new List<string> { header };
        updatedLines.AddRange(tasks.Select(parts => string.Join(",", parts)));
        File.WriteAllLines(filePath, updatedLines);

        PublishNewTaskNotification(serviceId, task[0], task[1]);

        return (taskId, task[1]); // Devolver o ID e descrição da tarefa
    }

    private static (string, string)? FinalizeTaskForClient(string serviceId, string clientId)
    {
        string directoryPath = Directory.GetCurrentDirectory();
        string filePath = Path.Combine(directoryPath, "Servico_" + serviceId + ".csv");

        var lines = File.ReadLines(filePath).ToList();
        var header = lines[0];
        var tasks = lines.Skip(1)
            .Select(line => line.Split(','))
            .ToList();

        var task = tasks.FirstOrDefault(parts => parts[2] == "Em curso" && parts[3] == clientId);
        if (task == null)
        {
            return null; // Tarefa não encontrada
        }

        task[2] = "Finalizada";

        var updatedLines = new List<string> { header };
        updatedLines.AddRange(tasks.Select(parts => string.Join(",", parts)));
        File.WriteAllLines(filePath, updatedLines);

        return (task[0], task[1]); // Devolver o ID e descrição da tarefa
    }

    private static void HandleClient(object? clientObj)
    {
        if (clientObj == null)
        {
            throw new ArgumentNullException(nameof(clientObj));
        }

        TcpClient client = clientObj as TcpClient;
        if (client == null)
        {
            throw new ArgumentException("Tipo de client inválido.");
        }

        var clientServices = File.ReadLines("clientes.csv")
            .Skip(1)
            .Select(line => line.Split(','))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        string clientId;
        string serviceId;

        using (StreamReader reader = new StreamReader(client.GetStream()))
        using (StreamWriter writer = new StreamWriter(client.GetStream()))
        {
            writer.AutoFlush = true;
            clientId = reader.ReadLine() ?? throw new InvalidOperationException("Client ID não pode ser nulo");
            Console.WriteLine($"Cliente {clientId} conectado.");
            mut.WaitOne();
            try
            {
                if (clientId.StartsWith("ADM_"))
                {
                    HandleAdminClient(clientId, reader, writer);
                }
                else
                {
                    HandleRegularClient(clientId, clientServices, reader, writer);
                }
            }
            finally
            {
                mut.ReleaseMutex();
            }
        }

        client.Close();
    }

    private static void HandleAdminClient(string clientId, StreamReader reader, StreamWriter writer)
    {
        writer.WriteLine("Digite a palavra-passe:");
        string password = reader.ReadLine() ?? string.Empty;

        if (!adminCredentials.TryGetValue(clientId, out string? correctPassword) || correctPassword != password)
        {
            writer.WriteLine("401 Não autorizado.");
            return;
        }
        writer.WriteLine("100 OK");

        while (true)
        {
            string request = reader.ReadLine();

            if (request == "QUIT")
            {
                writer.WriteLine("400 BYE.");
                break;
            }
            else if (request == "LISTA_SERVICO")
            {
                // Lógica para por nalista as informações do serviço
                string serviceInfo = "Informações do serviço"; // Placeholder
                writer.WriteLine(serviceInfo);
            }
            else if (request.StartsWith("CRIAR_TAREFA "))
            {
                string[] parts = request.Substring("CRIAR_TAREFA ".Length).Split(',');
                string serviceId = parts[0];
                string taskId = parts[1];
                string taskDescription = parts[2];
                writer.WriteLine(taskId);
                writer.WriteLine(taskDescription);

                // Lógica para criar uma nova tarefa
            }
            else if (request.StartsWith("ALOCA_MOTA "))
            {
                string[] parts = request.Substring("ALOCA_MOTA ".Length).Split(',');
                string serviceId = parts[0];
                string motoId = parts[1];
                string taskDescription = parts[2];
                writer.WriteLine(serviceId);
                writer.WriteLine(taskDescription);

                // Lógica para alocar uma mota
            }
            else if (request.StartsWith("ALOCA_PESSOA "))
            {
                string[] parts = request.Substring("ALOCA_PESSOA ".Length).Split(',');
                string serviceId = parts[0];
                string personId = parts[1];
                writer.WriteLine(serviceId);
                writer.WriteLine(personId);

                // Lógica para alocar uma pessoa
            }
            else
            {
                writer.WriteLine("405 Comando inválido.");
            }
        }
    }

    private static void HandleRegularClient(string clientId, Dictionary<string, string> clientServices, StreamReader reader, StreamWriter writer)
    {
        if (!clientServices.TryGetValue(clientId, out string serviceId))
        {
            writer.WriteLine("404 Not Found. Queres criar um novo cliente? (sim/nao)");
            string response = reader.ReadLine();

            if (response != null && response.ToLower() == "sim")
            {
                (clientId, serviceId) = AddNewClient(clientId);
                writer.WriteLine("200 Novo cliente criado, serviço: " + serviceId);
                writer.WriteLine("100 OK");
                writer.Flush();
            }
            else if (response != null && response.ToLower() == "nao")
            {
                return;
            }
                }

        while (true)
        {
            string request = reader.ReadLine();

            if (request == "STATUS")
            {
               // string taskId = request.Substring("STATUS". Length);
                var task = GetTaskInProgress(serviceId, clientId);
                if (task != null)
                {
                    writer.WriteLine($"210 Tarefa em curso {task.Value.TaskId}, {task.Value.Description}.");
                }
                else
                {
                    writer.WriteLine("410 Sem tarefa em curso.");
                }
            }
            else if (request == "LISTA")
            {
                var availableTasks = GetAvailableTasks(serviceId);
                if (availableTasks.Any())
                {
                    string tasks = string.Join(";", availableTasks.Select(t => $"209 Tarefa {t.Item1}, descrição: {t.Item2}"));
                    writer.WriteLine(tasks);
                }
                else
                {
                    writer.WriteLine("409 Sem tarefas disponiveis");
                }
            }
            else if (request.StartsWith("TAREFA "))
            {
                string taskId = request.Substring("TAREFA ".Length);
                var task = AssignTaskToClient(serviceId, taskId, clientId);
                if (task != null)
                {
                    writer.WriteLine($"211 Tarefa {task.Value.Item1} {task.Value.Item2} em curso");
                }
                else
                {
                    writer.WriteLine($"411 Tarefa {taskId} nao pode ser colocada em curso.");
                }
            }
            else if (request == "FINALIZADA")
            {
                var task = FinalizeTaskForClient(serviceId, clientId);
                if (task != null)
                {
                    writer.WriteLine($"202 Tarefa {task.Value.Item1} finalizada.");
                }
                else
                {
                    writer.WriteLine($"402 Erro: Não tens nenhuma tarefa em curso");
                }
            }
            else if (request == "QUIT")
            {
                writer.WriteLine("400 BYE.");
                break;
            }
            else
            {
                writer.WriteLine("405 Comando inválido.");
            }
        }
    }

    private static void PublishNewTaskNotification(string serviceId, string taskId, string taskDescription)
    {
        var factory = new ConnectionFactory() { HostName = "localhost" };
        using (var connection = factory.CreateConnection())
        using (var channel = connection.CreateModel())
        {
            channel.ExchangeDeclare(exchange: "tasks", type: "fanout");

            string message = $"{serviceId},{taskId},{taskDescription}";
            var body = Encoding.UTF8.GetBytes(message);

            channel.BasicPublish(exchange: "tasks",
                                 routingKey: "",
                                 basicProperties: null,
                                 body: body);
            Console.WriteLine(" [x] Sent {0}", message);
        }
    }
}
