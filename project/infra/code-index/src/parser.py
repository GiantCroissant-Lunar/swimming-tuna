"""Tree-sitter based AST parser for structural code extraction."""

from pathlib import Path
from typing import List, Optional, Tuple

from tree_sitter import Language, Parser, Tree, Node

from src.models import CodeChunk, Language as LangEnum, NodeType


# Language file extensions
LANGUAGE_EXTENSIONS = {
    LangEnum.CSHARP: [".cs"],
    LangEnum.JAVASCRIPT: [".js", ".jsx"],
    LangEnum.TYPESCRIPT: [".ts", ".tsx"],
    LangEnum.PYTHON: [".py"],
}

# Tree-sitter language modules
_LANGUAGE_MODULES = {
    LangEnum.CSHARP: "tree_sitter_c_sharp",
    LangEnum.JAVASCRIPT: "tree_sitter_javascript",
    LangEnum.TYPESCRIPT: "tree_sitter_typescript",  # typescript includes tsx
    LangEnum.PYTHON: "tree_sitter_python",
}


class TreeSitterParser:
    """Tree-sitter parser wrapper with language support."""

    def __init__(self):
        self._parsers: dict[LangEnum, Parser] = {}
        self._languages: dict[LangEnum, Language] = {}
        self._init_languages()

    def _init_languages(self):
        """Initialize tree-sitter languages."""
        for lang, module_name in _LANGUAGE_MODULES.items():
            try:
                if lang == LangEnum.CSHARP:
                    import tree_sitter_c_sharp as tspython

                    self._languages[lang] = Language(tspython.language())
                elif lang == LangEnum.JAVASCRIPT:
                    import tree_sitter_javascript as tspython

                    self._languages[lang] = Language(tspython.language())
                elif lang == LangEnum.TYPESCRIPT:
                    # TypeScript uses same parser as JavaScript for basic parsing
                    import tree_sitter_typescript as tspython

                    self._languages[lang] = Language(tspython.language_typescript())
                elif lang == LangEnum.PYTHON:
                    import tree_sitter_python as tspython

                    self._languages[lang] = Language(tspython.language())

                parser = Parser(self._languages[lang])
                self._parsers[lang] = parser
            except ImportError:
                # Language not available, skip
                continue

    def get_parser(self, language: LangEnum) -> Optional[Parser]:
        """Get parser for a language."""
        return self._parsers.get(language)

    def parse_file(self, file_path: Path, language: LangEnum) -> Optional[Tree]:
        """Parse a file and return AST tree."""
        parser = self._parsers.get(language)
        if not parser:
            return None

        try:
            content = file_path.read_text(encoding="utf-8", errors="ignore")
            return parser.parse(content.encode("utf-8"))
        except Exception:
            return None


class CodeExtractor:
    """Extracts structural code chunks from AST."""

    def __init__(self, parser: TreeSitterParser):
        self.parser = parser

    def extract_chunks(
        self, file_path: Path, source_root: Path, language: LangEnum
    ) -> List[CodeChunk]:
        """Extract all chunks from a file."""
        tree = self.parser.parse_file(file_path, language)
        if not tree:
            return []

        content = file_path.read_text(encoding="utf-8", errors="ignore")
        lines = content.split("\n")

        relative_path = str(file_path.relative_to(source_root))

        # Get namespace for C# files
        namespace = self._extract_namespace(tree.root_node, content)

        chunks = []
        chunk_nodes = self._find_chunk_nodes(tree.root_node, language)

        for node in chunk_nodes:
            chunk = self._create_chunk(
                node, content, lines, relative_path, namespace, language
            )
            if chunk:
                chunks.append(chunk)

        return chunks

    def _extract_namespace(self, root_node: Node, content: str) -> str:
        """Extract namespace from C# file."""
        namespace_node = None
        for child in self._walk_tree(root_node):
            if child.type == "namespace_declaration":
                namespace_node = child
                break

        if namespace_node:
            # Extract namespace name
            name_node = namespace_node.child_by_field_name("name")
            if name_node:
                return content[name_node.start_byte : name_node.end_byte]
        return ""

    def _walk_tree(self, node: Node):
        """Walk AST tree yielding all nodes."""
        yield node
        for child in node.children:
            yield from self._walk_tree(child)

    def _find_chunk_nodes(self, root_node: Node, language: LangEnum) -> List[Node]:
        """Find all nodes that should become chunks."""
        chunk_types = self._get_chunk_types(language)

        chunks = []
        for node in self._walk_tree(root_node):
            if node.type in chunk_types:
                chunks.append(node)

        return chunks

    def _get_chunk_types(self, language: LangEnum) -> set:
        """Get AST node types that should be chunked for a language."""
        if language == LangEnum.CSHARP:
            return {
                "class_declaration",
                "interface_declaration",
                "struct_declaration",
                "record_declaration",
                "enum_declaration",
                "method_declaration",
                "constructor_declaration",
                "property_declaration",
                "field_declaration",
                "event_declaration",
                "delegate_declaration",
            }
        elif language in (LangEnum.JAVASCRIPT, LangEnum.TYPESCRIPT):
            return {
                "class_declaration",
                "function_declaration",
                "method_definition",
                "arrow_function",
                "generator_function_declaration",
            }
        elif language == LangEnum.PYTHON:
            return {
                "class_definition",
                "function_definition",
                "async_function_definition",
            }
        return set()

    def _create_chunk(
        self,
        node: Node,
        content: str,
        lines: List[str],
        file_path: str,
        namespace: str,
        language: LangEnum,
    ) -> Optional[CodeChunk]:
        """Create a CodeChunk from an AST node."""
        node_text = content[node.start_byte : node.end_byte]
        if not node_text.strip():
            return None

        # Determine node type
        node_type = self._map_node_type(node.type, language)

        # Extract name
        name = self._extract_node_name(node, content, language)

        # Build fully qualified name
        if namespace and name:
            fully_qualified_name = f"{namespace}.{name}"
        else:
            fully_qualified_name = name or f"{node.type}_{node.start_point[0]}"

        # Line numbers are 1-based
        start_line = node.start_point[0] + 1
        end_line = node.end_point[0] + 1

        return CodeChunk(
            file_path=file_path,
            fully_qualified_name=fully_qualified_name,
            node_type=node_type,
            language=language,
            content=node_text,
            start_line=start_line,
            end_line=end_line,
            char_count=len(node_text),
            last_modified=None,  # Will be set from file stats
        )

    def _map_node_type(self, ts_type: str, language: LangEnum) -> NodeType:
        """Map tree-sitter node type to our NodeType enum."""
        type_mapping = {
            "class_declaration": NodeType.CLASS,
            "class_definition": NodeType.CLASS,
            "interface_declaration": NodeType.INTERFACE,
            "struct_declaration": NodeType.STRUCT,
            "record_declaration": NodeType.RECORD,
            "record_struct_declaration": NodeType.RECORD,
            "enum_declaration": NodeType.ENUM,
            "method_declaration": NodeType.METHOD,
            "method_definition": NodeType.METHOD,
            "function_definition": NodeType.FUNCTION,
            "function_declaration": NodeType.FUNCTION,
            "async_function_definition": NodeType.FUNCTION,
            "constructor_declaration": NodeType.CONSTRUCTOR,
            "property_declaration": NodeType.PROPERTY,
            "field_declaration": NodeType.FIELD,
            "event_declaration": NodeType.EVENT,
            "delegate_declaration": NodeType.DELEGATE,
            "arrow_function": NodeType.ARROW_FUNCTION,
            "namespace_declaration": NodeType.NAMESPACE,
        }
        return type_mapping.get(ts_type, NodeType.UNKNOWN)

    def _extract_node_name(self, node: Node, content: str, language: LangEnum) -> str:
        """Extract the name of a node (class name, method name, etc.)."""
        name_field = node.child_by_field_name("name")
        if name_field:
            return content[name_field.start_byte : name_field.end_byte]

        # Fallback: try to find identifier child
        for child in node.children:
            if child.type in ("identifier", "property_identifier", "method_identifier"):
                return content[child.start_byte : child.end_byte]

        return ""


def detect_language(file_path: Path) -> Optional[LangEnum]:
    """Detect language from file extension."""
    suffix = file_path.suffix.lower()
    for lang, extensions in LANGUAGE_EXTENSIONS.items():
        if suffix in extensions:
            return lang
    return None


def find_source_files(
    root_path: Path, languages: List[LangEnum]
) -> List[Tuple[Path, LangEnum]]:
    """Find all source files for given languages."""
    files = []

    extensions = set()
    for lang in languages:
        extensions.update(LANGUAGE_EXTENSIONS.get(lang, []))

    for ext in extensions:
        for file_path in root_path.rglob(f"*{ext}"):
            # Skip common non-source directories
            if any(part.startswith(".") for part in file_path.parts):
                continue
            if "node_modules" in file_path.parts:
                continue
            if "bin" in file_path.parts or "obj" in file_path.parts:
                continue
            if "__pycache__" in file_path.parts:
                continue

            lang = detect_language(file_path)
            if lang:
                files.append((file_path, lang))

    return files
