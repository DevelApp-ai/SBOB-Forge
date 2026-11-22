<?php

namespace Equiv;

class StatusClient
{
    public function fetchStatus(): string
    {
        $curl = curl_init("https://api.example.com/status");
        curl_setopt($curl, CURLOPT_RETURNTRANSFER, true);
        $body = curl_exec($curl);
        curl_close($curl);
        return $body;
    }
}
