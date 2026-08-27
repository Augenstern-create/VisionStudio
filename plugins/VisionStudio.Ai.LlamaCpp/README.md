# llama.cpp AI connector plugin

This optional plugin connects Vision Studio to a loopback `llama-server` process. It intentionally contains no model, llama.cpp executable, CUDA library, or other native runtime.

Build or download the plugin, extract it below the Vision Studio installation directory's `plugins` folder, then open **File > Settings > AI Models**. Add a local model and select:

- the plugin directory;
- `llama-server.exe` (or the Linux executable);
- a compatible GGUF model;
- the model's `mmproj` GGUF when image input is required.

Without `mmproj`, the model can plan a workflow from text and the OpenCV graph can still measure the original image, but the language model itself cannot inspect image pixels.
