using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class CxxParserTests
{
    private static CodeModel ParseC(string source) =>
        new C17SourceParser().Parse("sut/c/config_store.c", source);

    private static CodeModel ParseCpp(string source) =>
        new Cpp20SourceParser().Parse("sut/cpp/config_store.cpp", source);

    // ---- C ----------------------------------------------------------------

    [Fact]
    public void C_FunctionBodiesCollectInvocationsWithLines()
    {
        var model = ParseC("""
            #include <stdio.h>
            #include "internal.h"

            static int open_log(const char *path)
            {
                FILE *f = fopen(path, "a");
                if (f == NULL)
                    return -1;
                return 0;
            }

            int reload(void)
            {
                FILE *cfg = fopen("/etc/app/config.json", "r");
                unlink("/var/cache/last.json");
                return cfg != NULL;
            }
            """);
        Assert.Equal(new[] { "stdio.h", "internal.h" }, model.ImportList.ToArray());

        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var reload = Assert.Single(global.Methods, m => m.Name == "reload");
        var open = Assert.Single(reload.Invocations, i => i.Method == "fopen");
        Assert.Null(open.Receiver);
        Assert.Equal("/etc/app/config.json", open.Args[0].Value);
        Assert.True(open.Args[0].IsLiteral);
        var unlink = Assert.Single(reload.Invocations, i => i.Method == "unlink");
        Assert.Equal("/var/cache/last.json", unlink.Args[0].Value);

        var log = Assert.Single(global.Methods, m => m.Name == "open_log");
        Assert.Single(log.Invocations, i => i.Method == "fopen");
    }

    [Fact]
    public void C_PrototypesAndMacrosDoNotBecomeMethods()
    {
        var model = ParseC("""
            int helper(void);
            #define UNUSED(x) ((void)(x))

            int helper(void)
            {
                return system("true");
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var helper = Assert.Single(global.Methods);
        Assert.Equal("helper", helper.Name);
        Assert.Single(helper.Invocations);
    }

    [Fact]
    public void C_TestMacroBlocksBecomeCxxTestEntries()
    {
        var model = ParseC("""
            #include "store.h"

            TEST(Store, OpensConfigReadOnly)
            {
                FILE *f = fopen("/etc/app/config.json", "r");
                ASSERT_NE(f, NULL);
            }

            TEST_F(StoreFixture, WritesCache)
            {
                GTEST_SKIP();
            }
            """);
        var tests = Assert.Single(model.Types, t => t.Name == "<cxx-tests>");
        Assert.Equal(2, tests.Methods.Count);

        var first = tests.Methods[0];
        var attr = Assert.Single(first.Attributes);
        Assert.Equal("cxx.entry", attr.Name);
        Assert.Equal("TEST", attr.Args[0].Value);
        Assert.True(attr.Args[0].IsNamed("macro"));
        Assert.Equal("Store", attr.Args[1].Value);
        Assert.Equal("OpensConfigReadOnly", attr.Args[2].Value);
        Assert.Single(first.Invocations, i => i.Method == "fopen");
        Assert.Single(first.Invocations, i => i.Method == "ASSERT_NE");

        var second = tests.Methods[1];
        Assert.Equal("TEST_F", Assert.Single(second.Attributes).Args[0].Value);
        Assert.Single(second.Invocations, i => i.Method == "GTEST_SKIP");
    }

    [Fact]
    public void C_ExecArgvListsAndCompoundLiterals()
    {
        var model = ParseC("""
            int spawn_full(void)
            {
                char *const argv[] = {"/usr/bin/git", "status", NULL};
                execv(argv[0], argv);
                return 0;
            }

            int spawn_literal(void)
            {
                execvp("/usr/bin/git", (char *[]){"/usr/bin/git", "status", NULL});
                return 0;
            }

            int spawn_dynamic(int argc, char **argv)
            {
                execvp(argv[0], argv);
                return 0;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var full = Assert.Single(global.Methods, m => m.Name == "spawn_full");
        // argv[] initializer is a literal list → List kind (NULL dropped)
        var execv = Assert.Single(full.Invocations, i => i.Method == "execv");
        Assert.Equal(ArgKind.Identifier, execv.Args[1].Kind);

        var lit = Assert.Single(global.Methods, m => m.Name == "spawn_literal");
        var execvp = Assert.Single(lit.Invocations, i => i.Method == "execvp");
        Assert.Equal("/usr/bin/git", execvp.Args[0].Value);
        Assert.Equal(ArgKind.List, execvp.Args[1].Kind);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, execvp.Args[1].ListElements().ToArray());

        var dyn = Assert.Single(global.Methods, m => m.Name == "spawn_dynamic");
        var call = Assert.Single(dyn.Invocations, i => i.Method == "execvp");
        // argv (a variable) is identifier-dynamic — never treated as a literal
        Assert.Equal(ArgKind.Identifier, call.Args[0].Kind);
        Assert.Equal(ArgKind.Identifier, call.Args[1].Kind);
    }

    [Fact]
    public void C_PartialListAnchorsArgv0WhenLaterElementsAreDynamic()
    {
        var model = ParseC("""
            int spawn_partial(char *extra)
            {
                execvp("/usr/bin/git", (char *[]){"/usr/bin/git", extra, NULL});
                return 0;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var call = Assert.Single(global.Methods.Single().Invocations, i => i.Method == "execvp");
        Assert.Equal("/usr/bin/git", call.Args[0].Value);
        Assert.Equal(ArgKind.PartialList, call.Args[1].Kind);
        Assert.Equal("/usr/bin/git", call.Args[1].Value);
    }

    [Fact]
    public void C_OpenFlagMasksMergeIntoOneIdentifier()
    {
        var model = ParseC("""
            #include <fcntl.h>

            int setup(void)
            {
                int fd = open("/var/log/app.log", O_WRONLY | O_CREAT | O_APPEND, 0644);
                int dirfd = open("/var/cache/app", O_DIRECTORY);
                int excl = open("/var/lock/app", O_WRONLY | O_CREAT | O_EXCL);
                return fd + dirfd + excl;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var setup = Assert.Single(global.Methods);
        var open = Assert.Single(setup.Invocations,
            i => i.Method == "open" && i.Args[1].Value == "O_WRONLY|O_CREAT|O_APPEND");
        Assert.Equal(ArgKind.Identifier, open.Args[1].Kind);
        Assert.Equal("O_WRONLY|O_CREAT|O_APPEND", open.Args[1].Value);
        var dir = Assert.Single(setup.Invocations, i => i.Line == open.Line + 1 && i.Method == "open");
        Assert.Equal("O_DIRECTORY", dir.Args[1].Value);
        Assert.Equal("O_WRONLY|O_CREAT|O_EXCL",
            setup.Invocations.First(i => i.Line == open.Line + 2).Args[1].Value);
    }

    [Fact]
    public void C_SocketHoistingAttachesHostAndPortToConnectAndListen()
    {
        var model = ParseC("""
            #include <sys/socket.h>
            #include <arpa/inet.h>

            int dial(struct sockaddr_in *addr)
            {
                addr->sin_port = htons(443);
                addr->sin_addr.s_addr = inet_addr("10.0.0.5");
                return connect(3, (struct sockaddr *)addr, sizeof(*addr));
            }

            int serve(struct sockaddr_in *addr)
            {
                addr->sin_port = htons(8080);
                return listen(4, 8);
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var dial = Assert.Single(global.Methods, m => m.Name == "dial");
        var connect = Assert.Single(dial.Invocations, i => i.Method == "connect");
        var host = Assert.Single(connect.Args, a => a.IsNamed("host"));
        var port = Assert.Single(connect.Args, a => a.IsNamed("port"));
        Assert.Equal("10.0.0.5", host.Value);
        Assert.Equal("443", port.Value);

        var serve = Assert.Single(global.Methods, m => m.Name == "serve");
        var listen = Assert.Single(serve.Invocations, i => i.Method == "listen");
        Assert.Equal("8080", Assert.Single(listen.Args, a => a.IsNamed("port")).Value);
        Assert.DoesNotContain(listen.Args, a => a.IsNamed("host"));
    }

    [Fact]
    public void C_CallsInsideControlClausesAreCollected()
    {
        var model = ParseC("""
            int guarded(const char *host)
            {
                if (connect(3, host, 16) < 0)
                    return system("false");
                while (feof(0) == 0)
                    break;
                for (int i = 0; i < 3; i++)
                    popen("ls", "r");
                return 0;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var guarded = Assert.Single(global.Methods);
        Assert.Equal(4, guarded.Invocations.Count);
        Assert.Single(guarded.Invocations, i => i.Method == "connect");
        Assert.Single(guarded.Invocations, i => i.Method == "system");
        Assert.Single(guarded.Invocations, i => i.Method == "popen");
        Assert.Single(guarded.Invocations, i => i.Method == "feof");
    }

    [Fact]
    public void C_GetAddrInfoArgsClassified()
    {
        var model = ParseC("""
            #include <netdb.h>

            int resolve(void)
            {
                struct addrinfo hints, *res;
                getaddrinfo("api.example.com", "443", &hints, &res);
                getaddrinfo("internal.local", NULL, &hints, &res);
                return 0;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var calls = global.Methods.Single().Invocations.Where(i => i.Method == "getaddrinfo").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("api.example.com", calls[0].Args[0].Value);
        Assert.Equal("443", calls[0].Args[1].Value);
        // NULL literal arg is kept as Identifier (argv terminator drop applies to lists only)
        Assert.Equal("NULL", calls[1].Args[1].Value);
    }

    [Fact]
    public void C_UnterminatedStringIsHardError()
    {
        var ex = Assert.Throws<ParseHardError>(() => ParseC(
            "int bad(void) { const char *s = \"unterminated;\n return 0; }\n"));
        Assert.Contains("unterminated", ex.Message);
    }

    [Fact]
    public void C_UnbalancedBracesIsHardError()
    {
        Assert.Throws<ParseHardError>(() => ParseC(
            "int bad(void) { if (1) { return 0; }\n"));
    }

    // ---- C++ --------------------------------------------------------------

    [Fact]
    public void Cpp_NamespacesClassesMethodsAndQualifiedCalls()
    {
        var model = ParseCpp("""
            #include <cstdlib>
            #include <filesystem>

            namespace app::storage {

            class ConfigStore
            {
            public:
                void write(const char *path)
                {
                    std::filesystem::create_directory("/var/lib/app");
                    std::filesystem::remove("/var/cache/last.json");
                    std::system("true");
                }
            };

            } // namespace
            """);
        var store = Assert.Single(model.Types, t => t.Name == "ConfigStore");
        Assert.Equal("app::storage", store.Namespace);
        var write = Assert.Single(store.Methods);

        var mkdir = Assert.Single(write.Invocations, i => i.Method == "create_directory");
        Assert.Equal("std::filesystem", mkdir.Receiver);
        var rm = Assert.Single(write.Invocations, i => i.Method == "remove");
        Assert.Equal("std::filesystem", rm.Receiver);
        Assert.Equal("/var/cache/last.json", rm.Args[0].Value);
        var sys = Assert.Single(write.Invocations, i => i.Method == "system");
        Assert.Equal("std", sys.Receiver);
    }

    [Fact]
    public void Cpp_TrackedFstreamVariablesNormalizeMemberCallReceivers()
    {
        var model = ParseCpp("""
            #include <fstream>

            class Log
            {
            public:
                void append()
                {
                    std::ifstream in("/etc/app/config.json");
                    char buf[64];
                    in.read(buf, 64);

                    std::ofstream out("/var/log/app.log", std::ios::app);
                    out << "line";

                    std::fstream both("/var/lib/app/db", std::ios::in | std::ios::out);
                }
            };
            """);
        var log = Assert.Single(model.Types, t => t.Name == "Log");
        var append = Assert.Single(log.Methods);

        var ctor = Assert.Single(append.Invocations, i => i.Method == "ifstream");
        Assert.Equal("std", ctor.Receiver);
        Assert.Equal("/etc/app/config.json", ctor.Args[0].Value);

        var read = Assert.Single(append.Invocations, i => i.Method == "read");
        Assert.Equal("std::ifstream", read.Receiver);

        var ofctor = Assert.Single(append.Invocations, i => i.Method == "ofstream");
        Assert.Equal("std::ios::app", ofctor.Args[1].Value);

        var fctor = Assert.Single(append.Invocations, i => i.Method == "fstream");
        Assert.Equal("std::ios::in|std::ios::out", fctor.Args[1].Value);
    }

    [Fact]
    public void Cpp_NamespaceAliasesAreResolved()
    {
        var model = ParseCpp("""
            #include <filesystem>

            namespace fs = std::filesystem;

            class Cleaner
            {
            public:
                void purge()
                {
                    fs::remove_all("/var/cache/app");
                }
            };
            """);
        var cleaner = Assert.Single(model.Types, t => t.Name == "Cleaner");
        var purge = Assert.Single(cleaner.Methods);
        var call = Assert.Single(purge.Invocations, i => i.Method == "remove_all");
        Assert.Equal("std::filesystem", call.Receiver);
    }

    [Fact]
    public void Cpp_ConstructorArgumentsAreCollected()
    {
        var model = ParseCpp("""
            #include <string>

            class Store
            {
            public:
                Store(const std::string &root)
                    : root_(root)
                {
                    std::system("init");
                }

            private:
                std::string root_;
            };
            """);
        var store = Assert.Single(model.Types, t => t.Name == "Store");
        var ctor = Assert.Single(store.Methods, m => m.Name == "Store");
        Assert.Single(ctor.Invocations, i => i.Method == "system");
    }

    [Fact]
    public void Cpp_Catch2SectionsAndSkipInsideMacroBodies()
    {
        var model = ParseCpp("""
            #include <catch2/catch_test_macros.hpp>

            TEST_CASE("writes and reads", "[store]")
            {
                SECTION("write")
                {
                    std::system("write");
                }
                SKIP("not on this platform");
            }
            """);
        var tests = Assert.Single(model.Types, t => t.Name == "<cxx-tests>");
        var entry = Assert.Single(tests.Methods);
        var attr = Assert.Single(entry.Attributes);
        Assert.Equal("TEST_CASE", attr.Args[0].Value);
        Assert.Equal("writes and reads", attr.Args[1].Value);
        Assert.Contains(entry.Invocations, i => i.Method == "SECTION");
        Assert.Contains(entry.Invocations, i => i.Method == "SKIP");
        Assert.Contains(entry.Invocations, i => i.Method == "system");
    }

    [Fact]
    public void Cpp_TemplateClassAndMethodsParse()
    {
        var model = ParseCpp("""
            #include <vector>

            template <typename T>
            class Repo
            {
            public:
                void clear()
                {
                    std::vector<T> items;
                    (void)items;
                    std::system("clear");
                }
            };
            """);
        var repo = Assert.Single(model.Types, t => t.Name == "Repo");
        var clear = Assert.Single(repo.Methods);
        Assert.Single(clear.Invocations, i => i.Method == "system");
    }

    [Fact]
    public void Cpp_AsioResolverQualifiedCall()
    {
        var model = ParseCpp("""
            #include <boost/asio.hpp>

            void resolve_example()
            {
                boost::asio::ip::tcp::resolver resolver;
                resolver.resolve("api.example.com", "443");
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var resolve = Assert.Single(global.Methods);
        // resolver is NOT a tracked type here (no construction call form) — receiver stays the variable
        var call = Assert.Single(resolve.Invocations, i => i.Method == "resolve");
        Assert.Equal("api.example.com", call.Args[0].Value);
        Assert.Equal("443", call.Args[1].Value);
    }

    [Fact]
    public void Cpp_RawStringLiteral()
    {
        var model = ParseCpp("""
            #include <cstdlib>

            int run(void)
            {
                std::system(R"cmd(echo hi)cmd");
                return 0;
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var run = Assert.Single(global.Methods);
        var call = Assert.Single(run.Invocations, i => i.Method == "system");
        Assert.Equal("echo hi", call.Args[0].Value);
    }

    [Fact]
    public void Cpp_SocketHoistingWorksWithMemberSyntax()
    {
        var model = ParseCpp("""
            #include <netinet/in.h>

            int dial(struct sockaddr_in addr)
            {
                addr.sin_port = htons(9443);
                addr.sin_addr.s_addr = inet_addr("192.168.1.10");
                return connect(3, (struct sockaddr *)&addr, sizeof(addr));
            }
            """);
        var global = Assert.Single(model.Types, t => t.Name == "<global>");
        var dial = Assert.Single(global.Methods);
        var connect = Assert.Single(dial.Invocations, i => i.Method == "connect");
        Assert.Equal("192.168.1.10", Assert.Single(connect.Args, a => a.IsNamed("host")).Value);
        Assert.Equal("9443", Assert.Single(connect.Args, a => a.IsNamed("port")).Value);
    }

    [Fact]
    public void Cpp_ConcatenatedStringArgumentIsExpressionNotLiteral()
    {
        var model = ParseCpp("""
            #include <fstream>

            class Store
            {
            public:
                void loadUser(const std::string &user)
                {
                    std::ifstream in("/etc/app/users/" + user + ".json");
                }
            };
            """);
        var store = Assert.Single(model.Types, t => t.Name == "Store");
        var loadUser = Assert.Single(store.Methods);
        var ctor = Assert.Single(loadUser.Invocations, i => i.Method == "ifstream");
        Assert.Equal(ArgKind.Expression, ctor.Args[0].Kind);
    }
}
