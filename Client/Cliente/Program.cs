using System;
using System.IO;
using System.Net.Sockets;

class Cliente
{

    public static void Main()
    {
        TcpClient client = new TcpClient("127.0.0.1", 13000);

        using (StreamReader reader = new StreamReader(client.GetStream()))
        using (StreamWriter writer = new StreamWriter(client.GetStream()))
        {
            // Ask for client ID
            // Ask for client ID
            Console.Write("ID: ");
            string clientId = Console.ReadLine();

            // Send client ID
            writer.WriteLine(clientId);
            writer.Flush();

            // Read server responses until "100 OK"
            string response;

            do
            {
                response = reader.ReadLine();
                Console.WriteLine(response);

                if (response.StartsWith("404"))
                {
                    // Send a response when server sends "404 NOT FOUND"
                    writer.WriteLine(Console.ReadLine());
                    writer.Flush();
                }
            } while (response != "100 OK");


            while (true)
            {
                
                // Ask for an option
                Console.Write("(STATUS, LISTA, TAREFA(ID), FINALIZADA, QUIT): ");
                string option = Console.ReadLine();

                // Send the option to the server
                writer.WriteLine(option);
                writer.Flush();

                // Read server response
                Console.WriteLine(reader.ReadLine());

                if (option == "QUIT")
                {
                    client.Close();
                    break;
                }

            }
        }

        client.Close();
    }

}
