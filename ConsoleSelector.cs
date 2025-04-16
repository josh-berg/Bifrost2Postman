public static class ConsoleSelector
{
    public static List<int> SelectFromList(List<string> options, string prompt)
    {
        var selectedIndices = new HashSet<int>();
        int currentIndex = 0;
        ConsoleKey key;

        Console.CursorVisible = false;

        do
        {
            Console.Clear();
            Console.WriteLine(prompt);
            for (int i = 0; i < options.Count; i++)
            {
                bool isSelected = selectedIndices.Contains(i);
                bool isHighlighted = i == currentIndex;

                Console.Write(isHighlighted ? "> " : "  ");
                Console.Write(isSelected ? "[x] " : "[ ] ");
                Console.WriteLine(options[i]);
            }

            key = Console.ReadKey(true).Key;

            switch (key)
            {
                case ConsoleKey.UpArrow:
                    currentIndex = (currentIndex == 0) ? options.Count - 1 : currentIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                    currentIndex = (currentIndex + 1) % options.Count;
                    break;
                case ConsoleKey.Spacebar:
                    if (!selectedIndices.Add(currentIndex))
                        selectedIndices.Remove(currentIndex);
                    break;
            }

        } while (key != ConsoleKey.Enter);

        Console.CursorVisible = true;

        return selectedIndices.ToList();
    }
}
