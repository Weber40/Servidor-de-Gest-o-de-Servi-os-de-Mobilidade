using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;

class Servidor
{
    private static Mutex mut = new Mutex();
   // static Dictionary<string, List<Tuple<string, string>>> services = new Dictionary<string, List<Tuple<string, string>>> ();



    /// Função principal do programa que inicia o servidor e aceita conexões de clientes. <summary>
    
    
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
        var tasks = File.ReadLines(serviceId + ".csv")
            .Skip(1) 
            .Select(line => line.Split(','))
            .Where(parts => parts[2] == "Nao alocado") // Filtrar tarefas não alocadas
            .Select(parts => (TaskId: parts[0], Description: parts[1]))
            .ToList();

        return tasks;
    }

    private static (string, string)? AssignTaskToClient(string serviceId, string taskId, string clientId)
    {
        var lines = File.ReadLines(serviceId + ".csv").ToList();
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
        File.WriteAllLines(serviceId + ".csv", updatedLines);

        return (taskId, task[1]); // Devolver o ID e descrição da tarefa
    }

    private static (string, string)? FinalizeTaskForClient(string serviceId, string clientId)
    {
        var lines = File.ReadLines(serviceId + ".csv").ToList();
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
        File.WriteAllLines(serviceId + ".csv", updatedLines);

        return (task[0], task[1]); // Devolver o ID e descrição da tarefa
    }

    /* private static void AtualizarCSV(string clientId, string taskId, string taskDescription)
   {
       string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clientes.csv");
       var lines = File.ReadAllLines(path).ToList();                                      //
       
        // Adicionar nova linha no formato "ClienteID,TaskID,Descrição"
        lines.Add($"{clientId},{taskId},{taskDescription}");
        File.WriteAllLines(path, lines);
   }*/

    private static void HandleClient(object clientObj)
    {
        if (clientObj == null)
        {
            throw new ArgumentNullException(nameof(clientObj)); //Para tirar alguns warnings 
        }

        TcpClient client = clientObj as TcpClient;

        if (client == null)
        {
            throw new ArgumentException("Invalid client object type.");  //Para tirar mais warnings
        }

        //Carregar os clientes e os serviços
        var clientServices = File.ReadLines(("clientes.csv"))
            .Skip(1) 
            .Select(line => line.Split(','))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        string clientId;
        string serviceId;

        using (StreamReader reader = new StreamReader(client.GetStream()))
        using (StreamWriter writer = new StreamWriter(client.GetStream()))
        {
            writer.AutoFlush = true;
            clientId = reader.ReadLine() ?? throw new InvalidOperationException("Client ID cannot be null.");
            Console.WriteLine($"Cliente {clientId} conectado.");
            mut.WaitOne();
            try
            {
                if (clientServices.TryGetValue(clientId, out serviceId))
                {
                    writer.WriteLine("100 OK");
                }
                else
                {
                    writer.WriteLine("404 Not Found. Queres criar um novo cliente? (sim/nao)");
                    string response = reader.ReadLine();

                    if (response.ToLower() == "sim")
                    {
                        (clientId, serviceId) = AddNewClient(clientId);
                        writer.WriteLine("200 Novo cliente criado, serviço: " + serviceId);
                        writer.WriteLine("100 OK");
                        writer.Flush();

                    }
                    else if (response.ToLower() == "nao" && response != null)
                    {
                        client.Close();
                        return;
                    }

                }

                while (true)
                {
                    string request = reader.ReadLine();

                    if (request == "STATUS")
                    {
                        (string taskId, string taskDescription) = GetTaskInProgress(serviceId, clientId);
                        if (taskId != null)
                        {
                            writer.WriteLine($"210 Tarefa em curso {taskId}, {taskDescription}.");
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
            finally
            {
                mut.ReleaseMutex();
            }
        }

        client.Close();
    }
}