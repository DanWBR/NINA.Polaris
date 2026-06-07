#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "toupcam.h"

static void removeln(char* str)
{
    char* endstr = strchr(str, '\n');
    if (endstr)
        *endstr = '\0';
}

int main(int, char**)
{
    char str[1024], *endstr;
    ToupcamDeviceV2 arr[TOUPCAM_MAX];
    int n = 0;
    do {
        unsigned camnumber = Toupcam_EnumV2(arr);
        if (0 == camnumber)
        {
            printf("No camera found, CTRL-C to exit, ENTER to enum again:");
            fgets(str, 1023, stdin);
            continue;
        }

        for (int i = 0; i < camnumber; ++i)
            printf("%d: %s\n", i, arr[i].displayname);
        if (1 == camnumber)
            printf("Input number [0] to select camera, CTRL-C to exit, ENTER to enum again:");
        else
            printf("Input a number [%u~%u] to select camera, CTRL-C to exit, ENTER to enum again:", 0, camnumber - 1);
        if (fgets(str, 1023, stdin))
        {
            removeln(str);
            n = strtol(str, &endstr, 10);
            if ((endstr && *endstr) || str[0] == '\0')
                continue;
            if (n < camnumber && n >= 0)
                break;
            else
            {
                printf("Number out of range\n");
                return -1;
            }
        }
    } while (true);

    printf("Please input [on/off] to conctrl device power (CTRL-C to exit):");
    if (fgets(str, 1023, stdin))
    {
        removeln(str);
        if (strcasecmp(str, "on") == 0)
        {
            HRESULT hr = Toupcam_Enable(arr[n].id, 1);
            if (FAILED(hr))
            {
                printf("failed to turn on device power, hr = 0x%08x\n", hr);
                return -1;
            }
            return 0;
        }
        else if (strcasecmp(str, "off") == 0)
        {
            HRESULT hr = Toupcam_Enable(arr[n].id, 0);
            if (FAILED(hr))
            {
                printf("failed to turn off device power, hr = 0x%08x\n", hr);
                return -1;
            }
            return 0;
        }
    }
    return -1;
}
