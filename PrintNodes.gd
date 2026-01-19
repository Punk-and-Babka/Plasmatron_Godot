@tool
extends EditorScript

func _run():
	# Получаем корневой узел текущей сцены
	var root = get_editor_interface().get_edited_scene_root()
	
	if root == null:
		print("Нет открытой сцены!")
		return

	var result_text = ""
	# Запускаем рекурсию
	result_text = get_names_recursive(root, "", result_text)
	
	# Вывод в консоль
	print(result_text)
	
	# Копирование в буфер обмена
	DisplayServer.clipboard_set(result_text)
	print("--- Список с типами скопирован в буфер обмена! ---")

func get_names_recursive(node: Node, indent: String, text: String) -> String:
	# Получаем тип узла (например, "Button", "Control")
	var type_name = node.get_class()
	
	# Формируем строку: Имя (Тип)
	# Например: MainInterface (Control)
	text += indent + node.name + " (" + type_name + ")\n"
	
	# Проходим по всем детям
	for child in node.get_children():
		text = get_names_recursive(child, indent + "  ", text)
		
	return text
