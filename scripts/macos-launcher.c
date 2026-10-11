/* Start the unchanged .NET payload from the signed application bundle.
 * Managed assemblies are resources, not nested Mach-O code in Contents/MacOS.
 */
#include <mach-o/dyld.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static int cannot_start(void)
{
    fputs("INGLORIOUS Advanced could not start. Reinstall the application.\n", stderr);
    return 126;
}

int main(int argc, char **argv)
{
    uint32_t length = 0;
    if (argc < 1 || argv == NULL || _NSGetExecutablePath(NULL, &length) != -1 || length == 0)
        return cannot_start();
    char *raw = malloc(length);
    if (raw == NULL) return cannot_start();
    if (_NSGetExecutablePath(raw, &length) != 0) { free(raw); return cannot_start(); }
    char *resolved = realpath(raw, NULL);
    free(raw);
    if (resolved == NULL) return cannot_start();
    char *separator = strrchr(resolved, '/');
    if (separator == NULL) { free(resolved); return cannot_start(); }
    *separator = '\0';
    char payload[PATH_MAX];
    int written = snprintf(payload, sizeof(payload), "%s/../Resources/application/INGLORIOUS Advanced", resolved);
    free(resolved);
    if (written < 0 || (size_t)written >= sizeof(payload)) return cannot_start();
    argv[0] = payload;
    execv(payload, argv);
    return cannot_start();
}
