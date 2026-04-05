using System;
using System.Reflection;
using KiteConnect;

class Program {
    static void Main() {
        Console.WriteLine("Properties:");
        foreach (var prop in typeof(Tick).GetProperties()) {
            Console.WriteLine($"{prop.PropertyType.Name} {prop.Name}");
        }
        Console.WriteLine("Fields:");
        foreach (var field in typeof(Tick).GetFields()) {
            Console.WriteLine($"{field.FieldType.Name} {field.Name}");
        }
    }
}
