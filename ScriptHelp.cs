using Godot;

public partial class ScriptHelp : Window
{
    [Export] private RichTextLabel _label;

    public override void _Ready()
    {
        Title = "Справка по скриптам"; // Заголовок окна

        // Если забыли привязать в инспекторе, ищем автоматически
        if (_label == null) _label = GetNodeOrNull<RichTextLabel>("ContentLabel");

        // Обработка кнопки закрытия (крестик)
        CloseRequested += QueueFree;

        // Заполняем текст
        if (_label != null)
        {
            _label.Text = GetHelpText();
        }
    }

    private string GetHelpText()
    {
        return
            "[font_size=20][b]Справочник команд[/b][/font_size]\n\n" +

            "[b][color=green]GO(x, y)[/color][/b]\n" +
            "Движение в точку (мм).\n" +
            "Пример: [code]GO(150, 200)[/code]\n" +
            "Пример: [code]GO(50)[/code] (Y=0)\n\n" +

            "[b][color=green]SPEED(v)[/color][/b]\n" +
            "Скорость (мм/сек).\n" +
            "Пример: [code]SPEED(100)[/code]\n\n" +

            "[b][color=green]PAUSE(t)[/color][/b]\n" +
            "Пауза (сек).\n" +
            "Пример: [code]PAUSE(2.5)[/code]\n\n" +

            "[b][color=yellow]CYCLE(x1, y1, x2, y2, N, [T])[/color][/b]\n" +
            "Цикл туда-обратно N раз.\n" +
            "Последняя цифра [T] — пауза в точках (сек).\n" +
            "Она необязательна (по умолч. 0.5 сек).\n\n" +

            "Пример (без паузы):\n" +
            "[code]CYCLE(100, 100, 200, 100, 5)[/code]\n\n" +

            "Пример (с паузой 2 сек):\n" +
            "[code]CYCLE(100, 100, 200, 100, 5, 2)[/code]\n\n" +

            "[i]Сокращенно (только X):[/i]\n" +
            "[code]CYCLE(100, 200, 10)[/code]\n" +
            "[code]CYCLE(100, 200, 10, 1.5)[/code] (пауза 1.5с)\n\n" +

            "[b][color=gray]Комментарии[/color][/b]\n" +
            "[code]//[/code] или [code]#[/code] игнорируются.";
    }
}